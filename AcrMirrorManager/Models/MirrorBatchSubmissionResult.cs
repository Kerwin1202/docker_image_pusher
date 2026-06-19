namespace AcrMirrorManager.Models;

public sealed record MirrorBatchSubmissionResult(
    IReadOnlyList<string> SourceImages,
    IReadOnlyList<string> ExpectedRepositories,
    string Branch,
    string CommitSha,
    string CommitUrl,
    bool WorkflowDispatchRequested);
