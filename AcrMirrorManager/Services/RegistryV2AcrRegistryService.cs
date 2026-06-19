using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AcrMirrorManager.Models;
using AcrMirrorManager.Options;
using Microsoft.Extensions.Options;

namespace AcrMirrorManager.Services;

public sealed class RegistryV2AcrRegistryService : IAcrRegistryService, IRegistryV2RefreshService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] ManifestAcceptHeaders =
    [
        "application/vnd.docker.distribution.manifest.v2+json",
        "application/vnd.docker.distribution.manifest.list.v2+json",
        "application/vnd.oci.image.manifest.v1+json",
        "application/vnd.oci.image.index.v1+json"
    ];

    private static readonly SemaphoreSlim ProbeConcurrency = new(6);

    private readonly HttpClient _httpClient;
    private readonly RegistryV2PersistentCache _cache;
    private readonly IGitHubMirrorService _gitHubMirror;
    private readonly RegistryV2Options _registryOptions;
    private readonly AliyunAcrOptions _aliyunOptions;
    private readonly GitHubMirrorOptions _gitHubOptions;

    public RegistryV2AcrRegistryService(
        HttpClient httpClient,
        RegistryV2PersistentCache cache,
        IGitHubMirrorService gitHubMirror,
        IOptions<RegistryV2Options> registryOptions,
        IOptions<AliyunAcrOptions> aliyunOptions,
        IOptions<GitHubMirrorOptions> gitHubOptions)
    {
        _httpClient = httpClient;
        _cache = cache;
        _gitHubMirror = gitHubMirror;
        _registryOptions = registryOptions.Value;
        _aliyunOptions = aliyunOptions.Value;
        _gitHubOptions = gitHubOptions.Value;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AcrMirrorManager/1.0");
    }

    public bool SupportsDelete => false;

    public async Task<IReadOnlyList<AcrRepository>> ListRepositoriesAsync(string? search, bool forceRefresh, CancellationToken cancellationToken)
    {
        EnsureConfigured(requirePassword: false);

        if (forceRefresh)
        {
            await RefreshAllRepositoriesAsync(cancellationToken);
        }

        var document = await _cache.ReadAsync(cancellationToken);
        if (document.Repositories.Count == 0)
        {
            await RefreshAllRepositoriesAsync(cancellationToken);
            document = await _cache.ReadAsync(cancellationToken);
        }

        IReadOnlyList<AcrRepository> repositories = document.Repositories
            .OrderBy(static x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(repository => ToAcrRepository(repository, document))
            .ToList();

        if (!string.IsNullOrWhiteSpace(search))
        {
            repositories = repositories
                .Where(x => x.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || x.Summary.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return repositories;
    }

    public async Task<IReadOnlyList<AcrTag>> ListTagsAsync(string repoId, bool forceRefresh, CancellationToken cancellationToken)
    {
        EnsureConfigured(requirePassword: false);
        if (string.IsNullOrWhiteSpace(_registryOptions.Username)
            || string.IsNullOrWhiteSpace(_registryOptions.Password))
        {
            return [];
        }

        if (forceRefresh)
        {
            await RefreshRepositoryAsync(repoId, includeManifestProbe: true, cancellationToken);
        }

        var document = await _cache.ReadAsync(cancellationToken);
        if (!document.Tags.TryGetValue(repoId, out var cachedTags)
            && document.Repositories.Any(x => x.RepoId.Equals(repoId, StringComparison.OrdinalIgnoreCase)
                && x.Status == "已存在"))
        {
            await RefreshRepositoryAsync(repoId, includeManifestProbe: true, cancellationToken);
            document = await _cache.ReadAsync(cancellationToken);
            document.Tags.TryGetValue(repoId, out cachedTags);
        }

        return (cachedTags ?? [])
            .OrderBy(static x => x.Tag, StringComparer.OrdinalIgnoreCase)
            .Select(static x => new AcrTag(x.Tag, x.Digest, null, x.Status, null, null))
            .ToList();
    }

    public async Task TrackSubmittedImageAsync(string imageLine, CancellationToken cancellationToken)
    {
        EnsureConfigured(requirePassword: false);

        var mapping = ImageNameMapper.ToAliyunImageMapping(imageLine);
        var repoPath = RepositoryPath(mapping.RepositoryName);
        var now = DateTimeOffset.Now;
        var minutes = _registryOptions.PostSubmitRefreshMinutes.Length == 0
            ? [3, 5, 10, 20]
            : _registryOptions.PostSubmitRefreshMinutes;

        await _cache.UpdateAsync(document =>
        {
            UpsertRepository(document, mapping, repoPath, "未推送", now);

            foreach (var minute in minutes.Where(static x => x > 0).Distinct().OrderBy(static x => x))
            {
                document.RefreshJobs.Add(new RegistryV2RefreshJob
                {
                    RepoId = repoPath,
                    DueAt = now.AddMinutes(minute)
                });
            }
        }, cancellationToken);
    }

    public async Task TrackSubmittedImageAsync(
        string imageLine,
        string commitSha,
        string commitUrl,
        CancellationToken cancellationToken)
    {
        EnsureConfigured(requirePassword: false);

        if (string.IsNullOrWhiteSpace(commitSha))
        {
            await TrackSubmittedImageAsync(imageLine, cancellationToken);
            return;
        }

        var mapping = ImageNameMapper.ToAliyunImageMapping(imageLine);
        var repoPath = RepositoryPath(mapping.RepositoryName);
        var now = DateTimeOffset.Now;

        await _cache.UpdateAsync(document =>
        {
            UpsertRepository(document, mapping, repoPath, "等待 Action", now);

            var repository = document.Repositories.First(x =>
                x.RepoId.Equals(repoPath, StringComparison.OrdinalIgnoreCase));
            repository.LastMirrorCommitSha = commitSha;
            repository.LastMirrorCommitUrl = commitUrl;
            repository.LastWorkflowStatus = "waiting";
            repository.LastWorkflowConclusion = null;
            repository.LastWorkflowUrl = null;
            repository.LastWorkflowCheckedAt = null;
            repository.LastMirrorSubmittedAt = now;
            repository.LastMirrorCompletedAt = null;
            repository.LastMirrorFailedAt = null;

            document.MirrorActionJobs.RemoveAll(x =>
                x.CompletedAt is null
                && x.FailedAt is null
                && x.RepoId.Equals(repoPath, StringComparison.OrdinalIgnoreCase));
            document.RefreshJobs.RemoveAll(x =>
                x.CompletedAt is null
                && x.RepoId.Equals(repoPath, StringComparison.OrdinalIgnoreCase));

            document.MirrorActionJobs.Add(new RegistryV2MirrorActionJob
            {
                RepoId = repoPath,
                SourceImage = imageLine,
                CommitSha = commitSha,
                CommitUrl = commitUrl,
                WorkflowStatus = "waiting",
                NextActionCheckAt = now
            });
        }, cancellationToken);
    }

    public async Task RemoveTrackedImageAsync(string imageLine, CancellationToken cancellationToken)
    {
        EnsureConfigured(requirePassword: false);

        var mapping = ImageNameMapper.ToAliyunImageMapping(imageLine);
        var repoPath = RepositoryPath(mapping.RepositoryName);
        var normalizedImage = NormalizeImageLineForPendingRemoval(imageLine);

        await _cache.UpdateAsync(document =>
        {
            if (!document.PendingRemovedImages.Any(x =>
                    x.Equals(normalizedImage, StringComparison.OrdinalIgnoreCase)))
            {
                document.PendingRemovedImages.Add(normalizedImage);
            }

            document.Repositories.RemoveAll(x =>
                x.RepoId.Equals(repoPath, StringComparison.OrdinalIgnoreCase));
            document.Tags.Remove(repoPath);
            document.RefreshJobs.RemoveAll(x =>
                x.RepoId.Equals(repoPath, StringComparison.OrdinalIgnoreCase));
            document.MirrorActionJobs.RemoveAll(x =>
                x.RepoId.Equals(repoPath, StringComparison.OrdinalIgnoreCase));
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetPendingRemovedImagesAsync(CancellationToken cancellationToken)
    {
        var document = await _cache.ReadAsync(cancellationToken);
        return document.PendingRemovedImages
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task ClearPendingRemovedImagesAsync(IReadOnlyCollection<string> imageLines, CancellationToken cancellationToken)
    {
        var normalized = imageLines
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Select(NormalizeImageLineForPendingRemoval)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (normalized.Count == 0)
        {
            return;
        }

        await _cache.UpdateAsync(document =>
        {
            document.PendingRemovedImages.RemoveAll(x => normalized.Contains(x));
        }, cancellationToken);
    }

    public async Task RunMirrorActionJobsAsync(CancellationToken cancellationToken)
    {
        var document = await _cache.ReadAsync(cancellationToken);
        var now = DateTimeOffset.Now;
        var jobs = document.MirrorActionJobs
            .Where(x => x.CompletedAt is null
                && x.FailedAt is null
                && (x.WorkflowConclusion == "success"
                    ? (x.NextAcrProbeAt ?? now) <= now
                    : x.NextActionCheckAt <= now))
            .OrderBy(x => x.WorkflowConclusion == "success" ? x.NextAcrProbeAt ?? now : x.NextActionCheckAt)
            .Take(10)
            .ToList();

        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessMirrorActionJobAsync(job, cancellationToken);
        }
    }

    public async Task RunDueRefreshJobsAsync(CancellationToken cancellationToken)
    {
        var document = await _cache.ReadAsync(cancellationToken);
        var now = DateTimeOffset.Now;
        var jobs = document.RefreshJobs
            .Where(x => x.CompletedAt is null && x.DueAt <= now)
            .OrderBy(static x => x.DueAt)
            .Take(10)
            .ToList();

        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RefreshRepositoryAsync(job.RepoId, includeManifestProbe: true, cancellationToken);
            await _cache.UpdateAsync(updated =>
            {
                var cachedJob = updated.RefreshJobs.FirstOrDefault(x => x.Id == job.Id);
                if (cachedJob is not null)
                {
                    cachedJob.CompletedAt = DateTimeOffset.Now;
                }
            }, cancellationToken);
        }
    }

    private async Task ProcessMirrorActionJobAsync(RegistryV2MirrorActionJob job, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;

        if (job.WorkflowConclusion != "success")
        {
            var run = await _gitHubMirror.GetWorkflowRunForCommitAsync(job.CommitSha, cancellationToken);

            if (run is null)
            {
                await _cache.UpdateAsync(document =>
                {
                    var cachedJob = FindMirrorActionJob(document, job.Id);
                    if (cachedJob is null)
                    {
                        return;
                    }

                    cachedJob.WorkflowStatus = "waiting";
                    cachedJob.NextActionCheckAt = now.AddMinutes(1);
                    UpdateRepositoryWorkflow(document, cachedJob, "等待 Action", now);
                }, cancellationToken);
                return;
            }

            await _cache.UpdateAsync(document =>
            {
                var cachedJob = FindMirrorActionJob(document, job.Id);
                if (cachedJob is null)
                {
                    return;
                }

                cachedJob.WorkflowRunId = run.Id;
                cachedJob.WorkflowStatus = run.Status;
                cachedJob.WorkflowConclusion = run.Conclusion;
                cachedJob.WorkflowUrl = run.HtmlUrl;
                cachedJob.WorkflowCheckedAt = now;

                var status = run.Status == "completed"
                    ? run.Conclusion == "success" ? "Action 成功" : "Action 失败"
                    : "Action 运行中";

                UpdateRepositoryWorkflow(document, cachedJob, status, now);

                if (run.Status != "completed")
                {
                    cachedJob.NextActionCheckAt = now.AddMinutes(1);
                    return;
                }

                if (run.Conclusion == "success")
                {
                    cachedJob.NextAcrProbeAt = now;
                    return;
                }

                cachedJob.FailedAt = now;
                cachedJob.FailureMessage = $"GitHub Actions 结束状态：{run.Conclusion ?? "unknown"}";
                MarkRepositoryMirrorFailed(document, cachedJob, now);
            }, cancellationToken);

            if (run.Status != "completed" || run.Conclusion != "success")
            {
                return;
            }
        }

        var probe = await ProbeRepositoryAsync(job.RepoId, includeManifestProbe: true, cancellationToken);
        await _cache.UpdateAsync(document =>
        {
            var cachedJob = FindMirrorActionJob(document, job.Id);
            if (cachedJob is null)
            {
                return;
            }

            ApplyProbeResult(document, cachedJob.RepoId, probe.Status, probe.Tags, now);

            if (probe.Status == "已存在")
            {
                cachedJob.CompletedAt = now;
                cachedJob.NextAcrProbeAt = null;
                MarkRepositoryMirrorCompleted(document, cachedJob, now);
                return;
            }

            cachedJob.AcrProbeAttempts++;
            if (cachedJob.AcrProbeAttempts >= 3)
            {
                cachedJob.FailedAt = now;
                cachedJob.FailureMessage = $"Action 成功后 ACR 仍未确认：{probe.Status}";
                MarkRepositoryMirrorFailed(document, cachedJob, now);
                return;
            }

            cachedJob.NextAcrProbeAt = now.AddMinutes(cachedJob.AcrProbeAttempts == 1 ? 2 : 4);

            var repository = document.Repositories.FirstOrDefault(x =>
                x.RepoId.Equals(cachedJob.RepoId, StringComparison.OrdinalIgnoreCase));
            if (repository is not null)
            {
                repository.Status = "等待 ACR";
            }
        }, cancellationToken);
    }


    public async Task RunDailyMissingRefreshAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        if (now.Hour < _registryOptions.DailyMissingRefreshHour)
        {
            return;
        }

        var today = DateOnly.FromDateTime(now.DateTime).ToString("yyyy-MM-dd");
        var document = await _cache.ReadAsync(cancellationToken);
        if (document.LastMissingRefreshDate == today)
        {
            return;
        }

        await SyncImagesIntoCacheAsync(cancellationToken);

        document = await _cache.ReadAsync(cancellationToken);
        var missingRepoIds = document.Repositories
            .Where(static x => x.Status == "未推送")
            .Select(static x => x.RepoId)
            .ToList();

        foreach (var repoId in missingRepoIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RefreshRepositoryAsync(repoId, includeManifestProbe: false, cancellationToken);
        }

        await _cache.UpdateAsync(updated => updated.LastMissingRefreshDate = today, cancellationToken);
    }

    private async Task RefreshAllRepositoriesAsync(CancellationToken cancellationToken)
    {
        await SyncImagesIntoCacheAsync(cancellationToken);

        var document = await _cache.ReadAsync(cancellationToken);
        var repoIds = document.Repositories.Select(static x => x.RepoId).ToList();
        var tasks = repoIds.Select(repoId => RefreshRepositoryAsync(repoId, includeManifestProbe: false, cancellationToken));
        await Task.WhenAll(tasks);
    }

    private async Task SyncImagesIntoCacheAsync(CancellationToken cancellationToken)
    {
        var imagesText = await GetImagesTextAsync(cancellationToken);
        var document = await _cache.ReadAsync(cancellationToken);
        var pendingRemoved = document.PendingRemovedImages.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mappings = ImageNameMapper.ExtractImageLines(imagesText, _registryOptions.IncludeCommentedImages)
            .Where(imageLine => !pendingRemoved.Contains(NormalizeImageLineForPendingRemoval(imageLine)))
            .Select(ImageNameMapper.ToAliyunImageMapping)
            .GroupBy(static x => x.RepositoryName, StringComparer.OrdinalIgnoreCase)
            .Select(static x => x.First())
            .OrderBy(static x => x.RepositoryName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        await _cache.UpdateAsync(document =>
        {
            foreach (var mapping in mappings)
            {
                var repoPath = RepositoryPath(mapping.RepositoryName);
                var existing = document.Repositories.FirstOrDefault(x =>
                    x.RepoId.Equals(repoPath, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    UpsertRepository(document, mapping, repoPath, "未推送", DateTimeOffset.Now);
                }
                else
                {
                    existing.Summary = Summary(mapping, repoPath);
                    existing.Namespace = EffectiveNamespace();
                }
            }
        }, cancellationToken);
    }

    private async Task RefreshRepositoryAsync(string repoId, bool includeManifestProbe, CancellationToken cancellationToken)
    {
        await ProbeConcurrency.WaitAsync(cancellationToken);
        try
        {
            var probe = await ProbeRepositoryAsync(repoId, includeManifestProbe, cancellationToken);
            await _cache.UpdateAsync(document =>
            {
                ApplyProbeResult(document, repoId, probe.Status, probe.Tags, DateTimeOffset.Now);
            }, cancellationToken);
        }
        finally
        {
            ProbeConcurrency.Release();
        }
    }

    private static RegistryV2MirrorActionJob? FindMirrorActionJob(RegistryV2CacheDocument document, string jobId)
    {
        return document.MirrorActionJobs.FirstOrDefault(x => x.Id.Equals(jobId, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeImageLineForPendingRemoval(string imageLine)
    {
        var normalized = imageLine.Trim();
        while (normalized.StartsWith('#'))
        {
            normalized = normalized[1..].TrimStart();
        }

        return normalized;
    }

    private static void UpdateRepositoryWorkflow(
        RegistryV2CacheDocument document,
        RegistryV2MirrorActionJob job,
        string status,
        DateTimeOffset now)
    {
        var repository = document.Repositories.FirstOrDefault(x =>
            x.RepoId.Equals(job.RepoId, StringComparison.OrdinalIgnoreCase));
        if (repository is null)
        {
            return;
        }

        repository.Status = status;
        repository.LastMirrorCommitSha = job.CommitSha;
        repository.LastMirrorCommitUrl = job.CommitUrl;
        repository.LastWorkflowRunId = job.WorkflowRunId;
        repository.LastWorkflowStatus = job.WorkflowStatus;
        repository.LastWorkflowConclusion = job.WorkflowConclusion;
        repository.LastWorkflowUrl = job.WorkflowUrl;
        repository.LastWorkflowCheckedAt = job.WorkflowCheckedAt ?? now;
    }

    private static void MarkRepositoryMirrorCompleted(
        RegistryV2CacheDocument document,
        RegistryV2MirrorActionJob job,
        DateTimeOffset now)
    {
        var repository = document.Repositories.FirstOrDefault(x =>
            x.RepoId.Equals(job.RepoId, StringComparison.OrdinalIgnoreCase));
        if (repository is null)
        {
            return;
        }

        repository.LastMirrorCompletedAt = now;
        repository.LastMirrorFailedAt = null;
    }

    private static void MarkRepositoryMirrorFailed(
        RegistryV2CacheDocument document,
        RegistryV2MirrorActionJob job,
        DateTimeOffset now)
    {
        var repository = document.Repositories.FirstOrDefault(x =>
            x.RepoId.Equals(job.RepoId, StringComparison.OrdinalIgnoreCase));
        if (repository is null)
        {
            return;
        }

        repository.LastMirrorFailedAt = now;
    }

    private static void ApplyProbeResult(
        RegistryV2CacheDocument document,
        string repoId,
        string status,
        IReadOnlyList<AcrTag> tags,
        DateTimeOffset now)
    {
        var repository = document.Repositories.FirstOrDefault(x =>
            x.RepoId.Equals(repoId, StringComparison.OrdinalIgnoreCase));
        if (repository is not null)
        {
            repository.Status = status;
            repository.LastCheckedAt = now;
        }

        document.Tags[repoId] = tags
            .Select(static x => new RegistryV2CachedTag
            {
                Tag = x.Tag,
                Digest = x.Digest,
                Status = x.Status
            })
            .ToList();
    }

    public Task DeleteTagAsync(string repoId, string tag, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Registry V2 个人版模式只用于展示，不支持从页面删除远端 Tag。");
    }

    public Task DeleteRepositoryAsync(string repoId, string repoName, string repoNamespace, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Registry V2 个人版模式只用于展示，不支持从页面删除远端仓库。");
    }

    private async Task<(string Status, IReadOnlyList<AcrTag> Tags)> ProbeRepositoryAsync(
        string repoPath,
        bool includeManifestProbe,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_registryOptions.Username)
            || string.IsNullOrWhiteSpace(_registryOptions.Password))
        {
            return ("待配置登录", []);
        }

        try
        {
            using var response = await SendRegistryAsync(
                HttpMethod.Get,
                $"{RepositoryPathForUrl(repoPath)}/tags/list",
                scope: $"repository:{repoPath}:pull",
                cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return ("未推送", []);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return (response.StatusCode == HttpStatusCode.Unauthorized ? "认证失败" : "无权限", []);
            }

            if (!response.IsSuccessStatusCode)
            {
                return ($"HTTP {(int)response.StatusCode}", []);
            }

            var tagsResponse = await JsonSerializer.DeserializeAsync<RegistryTagsResponse>(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                JsonOptions,
                cancellationToken);
            var tags = tagsResponse?.Tags ?? [];
            var result = new List<AcrTag>();
            var manifestProbeLimit = includeManifestProbe ? Math.Max(0, _registryOptions.ManifestProbeLimit) : 0;

            foreach (var tag in tags.OrderBy(static x => x, StringComparer.OrdinalIgnoreCase))
            {
                string digest = string.Empty;
                var status = "Tag存在";

                if (result.Count < manifestProbeLimit)
                {
                    var manifest = await ProbeManifestAsync(repoPath, tag, cancellationToken);
                    digest = manifest.Digest;
                    status = manifest.Exists ? "可拉取" : "Tag存在";
                }

                result.Add(new AcrTag(tag, digest, null, status, null, null));
            }

            return ("已存在", result);
        }
        catch (Exception ex)
        {
            return ($"检查失败：{ex.Message}", []);
        }
    }

    private async Task<(bool Exists, string Digest)> ProbeManifestAsync(
        string repoPath,
        string tag,
        CancellationToken cancellationToken)
    {
        using var response = await SendRegistryAsync(
            HttpMethod.Head,
            $"{RepositoryPathForUrl(repoPath)}/manifests/{Uri.EscapeDataString(tag)}",
            scope: $"repository:{repoPath}:pull",
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return (false, string.Empty);
        }

        return (true, response.Headers.TryGetValues("Docker-Content-Digest", out var values)
            ? values.FirstOrDefault() ?? string.Empty
            : string.Empty);
    }

    private async Task<HttpResponseMessage> SendRegistryAsync(
        HttpMethod method,
        string path,
        string scope,
        CancellationToken cancellationToken)
    {
        var response = await SendRegistryRequestAsync(method, path, BasicAuthHeader(), cancellationToken);

        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        var challenge = response.Headers.WwwAuthenticate.FirstOrDefault(x =>
            x.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase));
        response.Dispose();

        if (challenge is null)
        {
            return await SendRegistryRequestAsync(method, path, BasicAuthHeader(), cancellationToken);
        }

        var token = await RequestBearerTokenAsync(challenge.ToString(), scope, cancellationToken);
        return await SendRegistryRequestAsync(method, path, new AuthenticationHeaderValue("Bearer", token), cancellationToken);
    }

    private async Task<HttpResponseMessage> SendRegistryRequestAsync(
        HttpMethod method,
        string path,
        AuthenticationHeaderValue authorization,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, $"{RegistryBaseAddress()}/v2/{path}");
        request.Headers.Authorization = authorization;

        foreach (var accept in ManifestAcceptHeaders)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        }

        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private async Task<string> RequestBearerTokenAsync(
        string challenge,
        string fallbackScope,
        CancellationToken cancellationToken)
    {
        var values = ParseWwwAuthenticate(challenge);
        if (!values.TryGetValue("realm", out var realm) || string.IsNullOrWhiteSpace(realm))
        {
            throw new InvalidOperationException("Registry 返回了 Bearer 认证挑战，但没有 realm。");
        }

        var query = new List<string>();
        if (values.TryGetValue("service", out var service) && !string.IsNullOrWhiteSpace(service))
        {
            query.Add($"service={Uri.EscapeDataString(service)}");
        }

        var scope = values.TryGetValue("scope", out var challengeScope) && !string.IsNullOrWhiteSpace(challengeScope)
            ? challengeScope
            : fallbackScope;
        query.Add($"scope={Uri.EscapeDataString(scope)}");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{realm}?{string.Join('&', query)}");
        request.Headers.Authorization = BasicAuthHeader();

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "获取 Registry Bearer Token 失败", cancellationToken);

        var tokenResponse = await JsonSerializer.DeserializeAsync<RegistryTokenResponse>(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            JsonOptions,
            cancellationToken);

        var token = tokenResponse?.Token ?? tokenResponse?.AccessToken;
        return string.IsNullOrWhiteSpace(token)
            ? throw new InvalidOperationException("Registry Token 响应中没有 token。")
            : token;
    }

    private async Task<string> GetImagesTextAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_gitHubOptions.Token))
        {
            try
            {
                return await GetImagesTextFromGitHubApiAsync(cancellationToken);
            }
            catch
            {
                return await GetImagesTextFromRawGitHubAsync(cancellationToken);
            }
        }

        return await GetImagesTextFromRawGitHubAsync(cancellationToken);
    }

    private async Task<string> GetImagesTextFromGitHubApiAsync(CancellationToken cancellationToken)
    {
        var escapedPath = string.Join('/', _gitHubOptions.ImagesPath.Split('/').Select(Uri.EscapeDataString));
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.github.com/repos/{_gitHubOptions.EffectiveOwner}/{_gitHubOptions.EffectiveRepository}/contents/{escapedPath}?ref={Uri.EscapeDataString(_gitHubOptions.Branch)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _gitHubOptions.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "读取 GitHub images.txt 失败", cancellationToken);

        var content = await JsonSerializer.DeserializeAsync<GitHubContentResponse>(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            JsonOptions,
            cancellationToken);

        if (content is null || string.IsNullOrWhiteSpace(content.Content))
        {
            throw new InvalidOperationException("GitHub images.txt 内容为空。");
        }

        var normalizedBase64 = content.Content.Replace("\n", string.Empty, StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal);
        return Encoding.UTF8.GetString(Convert.FromBase64String(normalizedBase64));
    }

    private async Task<string> GetImagesTextFromRawGitHubAsync(CancellationToken cancellationToken)
    {
        var escapedPath = string.Join('/', _gitHubOptions.ImagesPath.Split('/').Select(Uri.EscapeDataString));
        var url = $"https://raw.githubusercontent.com/{_gitHubOptions.EffectiveOwner}/{_gitHubOptions.EffectiveRepository}/{Uri.EscapeDataString(_gitHubOptions.Branch)}/{escapedPath}";
        return await _httpClient.GetStringAsync(url, cancellationToken);
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string message,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = response.Content is null
            ? string.Empty
            : await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException($"{message}：{(int)response.StatusCode} {body}");
    }

    private AuthenticationHeaderValue BasicAuthHeader()
    {
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_registryOptions.Username}:{_registryOptions.Password}"));
        return new AuthenticationHeaderValue("Basic", token);
    }

    private string RepositoryPath(string repositoryName)
    {
        return $"{EffectiveNamespace()}/{repositoryName}";
    }

    private static string RepositoryPathForUrl(string repoPath)
    {
        return string.Join('/', repoPath.Split('/').Select(Uri.EscapeDataString));
    }

    private string RegistryBaseAddress()
    {
        var registry = _registryOptions.Registry.Trim().TrimEnd('/');
        return registry.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || registry.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? registry
            : $"https://{registry}";
    }

    private void EnsureConfigured(bool requirePassword)
    {
        if (string.IsNullOrWhiteSpace(_registryOptions.Registry)
            || string.IsNullOrWhiteSpace(EffectiveNamespace())
            || (requirePassword && (string.IsNullOrWhiteSpace(_registryOptions.Username)
                || string.IsNullOrWhiteSpace(_registryOptions.Password))))
        {
            throw new InvalidOperationException("请先配置 RegistryV2:Registry、Namespace、Username 和 Password。Namespace 可复用 AliyunAcr:Namespace。");
        }
    }

    private string EffectiveNamespace()
    {
        return string.IsNullOrWhiteSpace(_registryOptions.Namespace)
            ? _aliyunOptions.Namespace
            : _registryOptions.Namespace;
    }

    private string RepositoriesCacheKey()
    {
        return $"registry-v2:repos:{RegistryBaseAddress()}:{EffectiveNamespace()}:{_gitHubOptions.RepositorySlug}:{_gitHubOptions.Branch}:{_gitHubOptions.ImagesPath}:{_registryOptions.IncludeCommentedImages}";
    }

    private string TagsCacheKey(string repoId)
    {
        return $"registry-v2:tags:{RegistryBaseAddress()}:{repoId}";
    }

    private static AcrRepository ToAcrRepository(RegistryV2CachedRepository repository, RegistryV2CacheDocument document)
    {
        var pendingRefreshJobs = document.RefreshJobs
            .Where(x => x.CompletedAt is null
                && x.RepoId.Equals(repository.RepoId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(static x => x.DueAt)
            .ToList();
        var pendingActionJobs = document.MirrorActionJobs
            .Where(x => x.CompletedAt is null
                && x.FailedAt is null
                && x.RepoId.Equals(repository.RepoId, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.WorkflowConclusion == "success" ? x.NextAcrProbeAt : x.NextActionCheckAt)
            .Where(static x => x.HasValue)
            .Select(static x => x!.Value)
            .OrderBy(static x => x)
            .ToList();
        var nextRefreshAt = pendingRefreshJobs
            .Select(static x => x.DueAt)
            .Concat(pendingActionJobs)
            .OrderBy(static x => x)
            .FirstOrDefault();

        return new AcrRepository(
            repository.RepoId,
            repository.Name,
            repository.Namespace,
            repository.Status,
            repository.Type,
            repository.Summary,
            repository.FirstSeenAt,
            repository.LastCheckedAt,
            nextRefreshAt == default ? null : nextRefreshAt,
            pendingRefreshJobs.Count + pendingActionJobs.Count,
            repository.LastMirrorCommitSha,
            repository.LastMirrorCommitUrl,
            repository.LastWorkflowStatus,
            repository.LastWorkflowConclusion,
            repository.LastWorkflowUrl,
            repository.LastWorkflowCheckedAt,
            repository.LastMirrorSubmittedAt,
            repository.LastMirrorCompletedAt,
            repository.LastMirrorFailedAt);
    }

    private void UpsertRepository(
        RegistryV2CacheDocument document,
        ImageMapping mapping,
        string repoPath,
        string status,
        DateTimeOffset now)
    {
        var repository = document.Repositories.FirstOrDefault(x =>
            x.RepoId.Equals(repoPath, StringComparison.OrdinalIgnoreCase));

        if (repository is null)
        {
            document.Repositories.Add(new RegistryV2CachedRepository
            {
                RepoId = repoPath,
                Name = mapping.RepositoryName,
                Namespace = EffectiveNamespace(),
                Status = status,
                Summary = Summary(mapping, repoPath),
                FirstSeenAt = now
            });
            return;
        }

        repository.Name = mapping.RepositoryName;
        repository.Namespace = EffectiveNamespace();
        repository.Summary = Summary(mapping, repoPath);
        repository.Status = status;
    }

    private string Summary(ImageMapping mapping, string repoPath)
    {
        return $"{mapping.SourceLine} -> {RegistryBaseAddress()}/{repoPath}:{mapping.Tag}";
    }

    private static Dictionary<string, string> ParseWwwAuthenticate(string challenge)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var value = challenge.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? challenge["Bearer ".Length..]
            : challenge;

        foreach (var part in SplitChallenge(value))
        {
            var index = part.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            var key = part[..index].Trim();
            var parsedValue = part[(index + 1)..].Trim().Trim('"');
            result[key] = parsedValue;
        }

        return result;
    }

    private static IEnumerable<string> SplitChallenge(string value)
    {
        var start = 0;
        var inQuotes = false;

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (value[i] == ',' && !inQuotes)
            {
                yield return value[start..i];
                start = i + 1;
            }
        }

        yield return value[start..];
    }

    private sealed record RegistryTagsResponse(
        string Name,
        IReadOnlyList<string>? Tags);

    private sealed record RegistryTokenResponse(
        string? Token,
        [property: JsonPropertyName("access_token")] string? AccessToken);

    private sealed record GitHubContentResponse(
        string Sha,
        string Content);
}
