using AcrMirrorManager.Models;

namespace AcrMirrorManager.Services;

public interface IAcrRegistryService
{
    bool SupportsDelete { get; }

    Task<IReadOnlyList<AcrRepository>> ListRepositoriesAsync(string? search, bool forceRefresh, CancellationToken cancellationToken);

    Task<IReadOnlyList<AcrTag>> ListTagsAsync(string repoId, bool forceRefresh, CancellationToken cancellationToken);

    Task DeleteTagAsync(string repoId, string tag, CancellationToken cancellationToken);

    Task DeleteRepositoryAsync(string repoId, string repoName, string repoNamespace, CancellationToken cancellationToken);
}
