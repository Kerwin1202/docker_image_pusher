using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AcrMirrorManager.Models;
using AcrMirrorManager.Options;
using Microsoft.Extensions.Options;

namespace AcrMirrorManager.Services;

public sealed class GitHubMirrorService : IGitHubMirrorService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string RerunMarkerPrefix = "# acr-mirror-manager rerun:";

    private readonly HttpClient _httpClient;
    private readonly GitHubMirrorOptions _options;

    public GitHubMirrorService(HttpClient httpClient, IOptions<GitHubMirrorOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AcrMirrorManager/1.0");
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _httpClient.DefaultRequestHeaders.Remove("X-GitHub-Api-Version");
        _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<MirrorSubmissionResult> SubmitImageAsync(string imageLine, bool commentOtherImages, CancellationToken cancellationToken)
    {
        return await SubmitImageAsync(imageLine, commentOtherImages, [], cancellationToken);
    }

    public async Task<MirrorSubmissionResult> SubmitImageAsync(
        string imageLine,
        bool commentOtherImages,
        IReadOnlyCollection<string> removeImageLines,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();

        var normalizedImage = NormalizeImageLine(imageLine);
        var file = await GetImagesFileAsync(cancellationToken);
        var updatedContent = UpdateImagesFile(file.Content, [normalizedImage], removeImageLines, commentOtherImages, forceRerunMarker: false);
        var commit = await PutImagesFileAsync(file, updatedContent, normalizedImage, cancellationToken);
        var workflowDispatchRequested = false;

        if (_options.TriggerWorkflowDispatch)
        {
            await DispatchWorkflowAsync(cancellationToken);
            workflowDispatchRequested = true;
        }

        return new MirrorSubmissionResult(
            normalizedImage,
            ImageNameMapper.ToAliyunRepositoryName(normalizedImage),
            _options.Branch,
            commit.Commit.Sha,
            commit.Commit.HtmlUrl,
            workflowDispatchRequested);
    }

    public async Task<MirrorBatchSubmissionResult> SubmitImagesAsync(
        IReadOnlyCollection<string> imageLines,
        bool commentOtherImages,
        CancellationToken cancellationToken)
    {
        return await SubmitImagesAsync(imageLines, commentOtherImages, [], cancellationToken);
    }

    public async Task<MirrorBatchSubmissionResult> SubmitImagesAsync(
        IReadOnlyCollection<string> imageLines,
        bool commentOtherImages,
        IReadOnlyCollection<string> removeImageLines,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();

        var normalizedImages = imageLines
            .Select(NormalizeImageLine)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedImages.Count == 0)
        {
            throw new ArgumentException("镜像地址不能为空。", nameof(imageLines));
        }

        var file = await GetImagesFileAsync(cancellationToken);
        var updatedContent = UpdateImagesFile(file.Content, normalizedImages, removeImageLines, commentOtherImages, forceRerunMarker: true);
        var commit = await PutImagesFileAsync(file, updatedContent, $"{normalizedImages.Count} images", cancellationToken);
        var workflowDispatchRequested = false;

        if (_options.TriggerWorkflowDispatch)
        {
            await DispatchWorkflowAsync(cancellationToken);
            workflowDispatchRequested = true;
        }

        return new MirrorBatchSubmissionResult(
            normalizedImages,
            normalizedImages.Select(ImageNameMapper.ToAliyunRepositoryName).ToList(),
            _options.Branch,
            commit.Commit.Sha,
            commit.Commit.HtmlUrl,
            workflowDispatchRequested);
    }

    public async Task<GitHubWorkflowRun?> GetWorkflowRunForCommitAsync(string commitSha, CancellationToken cancellationToken)
    {
        EnsureConfigured();

        if (string.IsNullOrWhiteSpace(commitSha))
        {
            return null;
        }

        var path = $"repos/{_options.EffectiveOwner}/{_options.EffectiveRepository}/actions/runs"
            + $"?head_sha={Uri.EscapeDataString(commitSha)}"
            + $"&branch={Uri.EscapeDataString(_options.Branch)}"
            + "&per_page=10";

        using var request = CreateRequest(HttpMethod.Get, path);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"查询 GitHub Actions 状态失败：{(int)response.StatusCode} {body}");
        }

        var payload = JsonSerializer.Deserialize<GitHubWorkflowRunsResponse>(body, JsonOptions);
        var workflowFile = $"/{_options.WorkflowFile}";
        var run = payload?.WorkflowRuns
            .OrderByDescending(static x => x.CreatedAt)
            .FirstOrDefault(x => x.Path.EndsWith(workflowFile, StringComparison.OrdinalIgnoreCase))
            ?? payload?.WorkflowRuns.OrderByDescending(static x => x.CreatedAt).FirstOrDefault();

        return run is null
            ? null
            : new GitHubWorkflowRun(
                run.Id,
                run.Name,
                run.Status,
                run.Conclusion,
                run.HtmlUrl,
                run.Path,
                run.CreatedAt,
                run.UpdatedAt);
    }

    private async Task<GitHubContentFile> GetImagesFileAsync(CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, ContentsUri());
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"读取 GitHub images.txt 失败：{(int)response.StatusCode} {body}");
        }

        var file = JsonSerializer.Deserialize<GitHubContentResponse>(body, JsonOptions)
            ?? throw new InvalidOperationException("GitHub 返回的 images.txt 内容为空。");

        var normalizedBase64 = file.Content.Replace("\n", string.Empty, StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal);

        return new GitHubContentFile(
            file.Sha,
            Encoding.UTF8.GetString(Convert.FromBase64String(normalizedBase64)));
    }

    private async Task<GitHubPutContentResponse> PutImagesFileAsync(
        GitHubContentFile currentFile,
        string updatedContent,
        string imageLine,
        CancellationToken cancellationToken)
    {
        var payload = new GitHubPutContentRequest(
            $"mirror: request {imageLine}",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(updatedContent)),
            currentFile.Sha,
            _options.Branch);

        using var request = CreateRequest(HttpMethod.Put, ContentsUri());
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"更新 GitHub images.txt 失败：{(int)response.StatusCode} {body}");
        }

        return JsonSerializer.Deserialize<GitHubPutContentResponse>(body, JsonOptions)
            ?? throw new InvalidOperationException("GitHub 更新成功，但没有返回 commit 信息。");
    }

    private async Task DispatchWorkflowAsync(CancellationToken cancellationToken)
    {
        var path = $"repos/{_options.EffectiveOwner}/{_options.EffectiveRepository}/actions/workflows/{Uri.EscapeDataString(_options.WorkflowFile)}/dispatches";
        var payload = JsonSerializer.Serialize(new { @ref = _options.Branch }, JsonOptions);

        using var request = CreateRequest(HttpMethod.Post, path);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"触发 GitHub workflow_dispatch 失败：{(int)response.StatusCode} {body}");
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);
        return request;
    }

    private string ContentsUri()
    {
        var escapedPath = string.Join('/', _options.ImagesPath.Split('/').Select(Uri.EscapeDataString));
        return $"repos/{_options.EffectiveOwner}/{_options.EffectiveRepository}/contents/{escapedPath}?ref={Uri.EscapeDataString(_options.Branch)}";
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.EffectiveOwner)
            || string.IsNullOrWhiteSpace(_options.EffectiveRepository)
            || string.IsNullOrWhiteSpace(_options.Branch)
            || string.IsNullOrWhiteSpace(_options.ImagesPath)
            || string.IsNullOrWhiteSpace(_options.Token))
        {
            throw new InvalidOperationException("请先配置 GitHubMirror:RepositoryUrl 和 Token。");
        }
    }

    private static string UpdateImagesFile(
        string currentContent,
        IReadOnlyCollection<string> imageLines,
        IReadOnlyCollection<string> removeImageLines,
        bool commentOtherImages,
        bool forceRerunMarker)
    {
        var normalized = currentContent.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
        var hadTrailingNewline = normalized.EndsWith('\n');
        var lines = normalized.Split('\n').ToList();

        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (forceRerunMarker)
        {
            lines.RemoveAll(line => line.TrimStart().StartsWith(RerunMarkerPrefix, StringComparison.OrdinalIgnoreCase));
        }

        var requested = imageLines
            .Select(NormalizeImageLine)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var removedImages = removeImageLines
            .Select(NormalizeImageLine)
            .Where(image => requested.All(requestedImage => !ImageEquals(requestedImage, image)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var found = requested.ToDictionary(static image => image, static _ => false, StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var uncommented = Uncomment(line);

            if (removedImages.Any(image => ImageEquals(uncommented, image)))
            {
                lines.RemoveAt(i);
                i--;
                continue;
            }

            var requestedImage = requested.FirstOrDefault(image => ImageEquals(uncommented, image));
            if (requestedImage is not null)
            {
                lines[i] = requestedImage;
                found[requestedImage] = true;
                continue;
            }

            if (commentOtherImages && IsEnabledImageLine(line))
            {
                lines[i] = "#" + line;
            }
        }

        var missing = requested.Where(image => !found[image]).ToList();
        if (missing.Count > 0 && lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.Add(string.Empty);
        }

        foreach (var image in missing)
        {
            lines.Add(image);
        }

        if (forceRerunMarker)
        {
            lines.Add($"{RerunMarkerPrefix} {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
        }

        var output = string.Join('\n', lines);
        return hadTrailingNewline || output.Length > 0 ? output + "\n" : output;
    }

    private static string NormalizeImageLine(string imageLine)
    {
        var normalized = imageLine.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("镜像地址不能为空。", nameof(imageLine));
        }

        if (normalized.StartsWith('#'))
        {
            normalized = Uncomment(normalized);
        }

        return normalized;
    }

    private static bool IsEnabledImageLine(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.Length > 0 && !trimmed.StartsWith('#');
    }

    private static string Uncomment(string line)
    {
        var trimmed = line.Trim();
        while (trimmed.StartsWith('#'))
        {
            trimmed = trimmed[1..].TrimStart();
        }

        return trimmed;
    }

    private static bool ImageEquals(string left, string right)
    {
        return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private sealed record GitHubContentFile(string Sha, string Content);

    private sealed record GitHubContentResponse(
        string Sha,
        string Content);

    private sealed record GitHubPutContentRequest(
        string Message,
        string Content,
        string Sha,
        string Branch);

    private sealed record GitHubPutContentResponse(
        GitHubCommit Commit);

    private sealed record GitHubCommit(
        string Sha,
        [property: JsonPropertyName("html_url")] string HtmlUrl);

    private sealed record GitHubWorkflowRunsResponse(
        [property: JsonPropertyName("workflow_runs")] IReadOnlyList<GitHubWorkflowRunResponse> WorkflowRuns);

    private sealed record GitHubWorkflowRunResponse(
        long Id,
        string Name,
        string Status,
        string? Conclusion,
        string Path,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt,
        [property: JsonPropertyName("updated_at")] DateTimeOffset? UpdatedAt);
}
