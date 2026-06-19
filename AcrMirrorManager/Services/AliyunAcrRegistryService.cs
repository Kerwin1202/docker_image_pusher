using AcrMirrorManager.Models;
using AcrMirrorManager.Options;
using AlibabaCloud.OpenApiClient.Models;
using AlibabaCloud.SDK.Cr20181201;
using AlibabaCloud.SDK.Cr20181201.Models;
using Microsoft.Extensions.Options;

namespace AcrMirrorManager.Services;

public sealed class AliyunAcrRegistryService : IAcrRegistryService
{
    private readonly AliyunAcrOptions _options;
    private readonly Lazy<Client> _client;

    public AliyunAcrRegistryService(IOptions<AliyunAcrOptions> options)
    {
        _options = options.Value;
        _client = new Lazy<Client>(CreateClient);
    }

    public bool SupportsDelete => true;

    public async Task<IReadOnlyList<AcrRepository>> ListRepositoriesAsync(string? search, bool forceRefresh, CancellationToken cancellationToken)
    {
        EnsureConfigured();

        var pageNo = 1;
        var repositories = new List<AcrRepository>();
        int? totalCount = null;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = await _client.Value.ListRepositoryAsync(new ListRepositoryRequest
            {
                InstanceId = _options.InstanceId,
                RepoNamespaceName = EmptyToNull(_options.Namespace),
                RepoName = EmptyToNull(search),
                PageNo = pageNo,
                PageSize = _options.PageSize
            });

            var body = response.Body;
            totalCount ??= ParseInt(body?.TotalCount);

            foreach (var repo in body?.Repositories ?? [])
            {
                repositories.Add(new AcrRepository(
                    repo.RepoId ?? string.Empty,
                    repo.RepoName ?? string.Empty,
                    repo.RepoNamespaceName ?? string.Empty,
                    repo.RepoStatus ?? string.Empty,
                    repo.RepoType ?? string.Empty,
                    repo.Summary ?? string.Empty,
                    FromUnixMilliseconds(repo.CreateTime),
                    FromUnixMilliseconds(repo.ModifiedTime),
                    null,
                    0,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null));
            }

            pageNo++;
        }
        while (totalCount.HasValue && repositories.Count < totalCount.Value);

        return repositories;
    }

    public async Task<IReadOnlyList<AcrTag>> ListTagsAsync(string repoId, bool forceRefresh, CancellationToken cancellationToken)
    {
        EnsureConfigured();

        var pageNo = 1;
        var tags = new List<AcrTag>();
        int? totalCount = null;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = await _client.Value.ListRepoTagAsync(new ListRepoTagRequest
            {
                InstanceId = _options.InstanceId,
                RepoId = repoId,
                PageNo = pageNo,
                PageSize = _options.PageSize
            });

            var body = response.Body;
            totalCount ??= ParseInt(body?.TotalCount);

            foreach (var image in body?.Images ?? [])
            {
                tags.Add(new AcrTag(
                    image.Tag ?? string.Empty,
                    image.Digest ?? string.Empty,
                    image.ImageSize,
                    image.Status ?? string.Empty,
                    ParseDateTimeOffset(image.ImageCreate),
                    ParseDateTimeOffset(image.ImageUpdate)));
            }

            pageNo++;
        }
        while (totalCount.HasValue && tags.Count < totalCount.Value);

        return tags;
    }

    public async Task DeleteTagAsync(string repoId, string tag, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        cancellationToken.ThrowIfCancellationRequested();

        await _client.Value.DeleteRepoTagAsync(new DeleteRepoTagRequest
        {
            InstanceId = _options.InstanceId,
            RepoId = repoId,
            Tag = tag
        });
    }

    public async Task DeleteRepositoryAsync(string repoId, string repoName, string repoNamespace, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        cancellationToken.ThrowIfCancellationRequested();

        await _client.Value.DeleteRepositoryAsync(new DeleteRepositoryRequest
        {
            InstanceId = _options.InstanceId,
            RepoId = repoId,
            RepoName = repoName,
            RepoNamespaceName = repoNamespace
        });
    }

    private Client CreateClient()
    {
        EnsureConfigured();

        return new Client(new Config
        {
            AccessKeyId = _options.AccessKeyId,
            AccessKeySecret = _options.AccessKeySecret,
            RegionId = _options.RegionId,
            Endpoint = EmptyToNull(_options.Endpoint)
        });
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.AccessKeyId)
            || string.IsNullOrWhiteSpace(_options.AccessKeySecret)
            || string.IsNullOrWhiteSpace(_options.RegionId)
            || string.IsNullOrWhiteSpace(_options.InstanceId))
        {
            throw new InvalidOperationException("请先配置 AliyunAcr:AccessKeyId、AccessKeySecret、RegionId 和 InstanceId。");
        }
    }

    private static string? EmptyToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static int? ParseInt(string? value)
    {
        return int.TryParse(value, out var parsed) ? parsed : null;
    }

    private static DateTimeOffset? FromUnixMilliseconds(long? value)
    {
        return value.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value).ToLocalTime() : null;
    }

    private static DateTimeOffset? ParseDateTimeOffset(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, out var parsed) ? parsed.ToLocalTime() : null;
    }
}
