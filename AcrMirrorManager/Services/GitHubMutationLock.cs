namespace AcrMirrorManager.Services;

public sealed class GitHubMutationLock
{
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public Task WaitAsync(CancellationToken cancellationToken) => _mutex.WaitAsync(cancellationToken);

    public void Release() => _mutex.Release();
}
