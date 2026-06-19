namespace AcrMirrorManager.Models;

public sealed record AcrRepository(
    string RepoId,
    string Name,
    string Namespace,
    string Status,
    string Type,
    string Summary,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? NextRefreshAt,
    int PendingRefreshCount,
    string? LastMirrorCommitSha,
    string? LastMirrorCommitUrl,
    string? LastWorkflowStatus,
    string? LastWorkflowConclusion,
    string? LastWorkflowUrl,
    DateTimeOffset? LastWorkflowCheckedAt,
    DateTimeOffset? LastMirrorSubmittedAt,
    DateTimeOffset? LastMirrorCompletedAt,
    DateTimeOffset? LastMirrorFailedAt);
