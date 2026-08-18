namespace AcrMirrorManager.Models;

public sealed record ScheduledImagesUpdateResult(
    IReadOnlyList<string> SourceImages,
    bool Enabled,
    int ChangedCount,
    string Branch,
    string? CommitSha,
    string? CommitUrl);
