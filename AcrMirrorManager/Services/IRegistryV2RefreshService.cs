namespace AcrMirrorManager.Services;

public interface IRegistryV2RefreshService
{
    Task TrackSubmittedImageAsync(string imageLine, CancellationToken cancellationToken);

    Task TrackSubmittedImageAsync(string imageLine, string commitSha, string commitUrl, CancellationToken cancellationToken);

    Task RemoveTrackedImageAsync(string imageLine, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetPendingRemovedImagesAsync(CancellationToken cancellationToken);

    Task ClearPendingRemovedImagesAsync(IReadOnlyCollection<string> imageLines, CancellationToken cancellationToken);

    Task RunMirrorActionJobsAsync(CancellationToken cancellationToken);

    Task RunDueRefreshJobsAsync(CancellationToken cancellationToken);

    Task RunDailyMissingRefreshAsync(CancellationToken cancellationToken);
}
