namespace AcrMirrorManager.Models;

public sealed record ScheduledImageUpdateResult(
    string SourceImage,
    bool Enabled,
    bool Changed,
    string Branch,
    string? CommitSha,
    string? CommitUrl);
