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
}
