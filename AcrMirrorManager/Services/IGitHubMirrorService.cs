using AcrMirrorManager.Models;

namespace AcrMirrorManager.Services;

public interface IGitHubMirrorService
{
    Task<MirrorSubmissionResult> SubmitImageAsync(string imageLine, bool commentOtherImages, CancellationToken cancellationToken);

    Task<MirrorSubmissionResult> SubmitImageAsync(
        string imageLine,
        bool commentOtherImages,
        IReadOnlyCollection<string> removeImageLines,
        CancellationToken cancellationToken);

    Task<MirrorBatchSubmissionResult> SubmitImagesAsync(IReadOnlyCollection<string> imageLines, bool commentOtherImages, CancellationToken cancellationToken);

    Task<MirrorBatchSubmissionResult> SubmitImagesAsync(
        IReadOnlyCollection<string> imageLines,
        bool commentOtherImages,
        IReadOnlyCollection<string> removeImageLines,
        CancellationToken cancellationToken);

    Task<GitHubWorkflowRun?> GetWorkflowRunForCommitAsync(string commitSha, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ListScheduledImagesAsync(CancellationToken cancellationToken);

    Task<ScheduledImageUpdateResult> SetScheduledImageAsync(
        string imageLine,
        bool enabled,
        CancellationToken cancellationToken);

    Task<ScheduledImagesUpdateResult> SetScheduledImagesAsync(
        IReadOnlyCollection<string> imageLines,
        bool enabled,
        CancellationToken cancellationToken);
}
