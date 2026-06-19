namespace AcrMirrorManager.Models;

public sealed record MirrorSubmissionResult(
    string SourceImage,
    string ExpectedRepository,
    string Branch,
    string CommitSha,
    string CommitUrl,
    bool WorkflowDispatchRequested);
