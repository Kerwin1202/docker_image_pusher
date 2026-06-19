namespace AcrMirrorManager.Models;

public sealed record GitHubWorkflowRun(
    long Id,
    string Name,
    string Status,
    string? Conclusion,
    string HtmlUrl,
    string Path,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt);
