namespace AcrMirrorManager.Models;

public sealed class RegistryV2CacheDocument
{
    public List<RegistryV2CachedRepository> Repositories { get; set; } = [];

    public Dictionary<string, List<RegistryV2CachedTag>> Tags { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<RegistryV2RefreshJob> RefreshJobs { get; set; } = [];

    public List<RegistryV2MirrorActionJob> MirrorActionJobs { get; set; } = [];

    public List<string> PendingRemovedImages { get; set; } = [];

    public string? LastMissingRefreshDate { get; set; }
}

public sealed class RegistryV2CachedRepository
{
    public string RepoId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Namespace { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string Type { get; set; } = "RegistryV2";

    public string Summary { get; set; } = string.Empty;

    public DateTimeOffset FirstSeenAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset? LastCheckedAt { get; set; }

    public string? LastMirrorCommitSha { get; set; }

    public string? LastMirrorCommitUrl { get; set; }

    public long? LastWorkflowRunId { get; set; }

    public string? LastWorkflowStatus { get; set; }

    public string? LastWorkflowConclusion { get; set; }

    public string? LastWorkflowUrl { get; set; }

    public DateTimeOffset? LastWorkflowCheckedAt { get; set; }

    public DateTimeOffset? LastMirrorSubmittedAt { get; set; }

    public DateTimeOffset? LastMirrorCompletedAt { get; set; }

    public DateTimeOffset? LastMirrorFailedAt { get; set; }
}

public sealed class RegistryV2CachedTag
{
    public string Tag { get; set; } = string.Empty;

    public string Digest { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;
}

public sealed class RegistryV2RefreshJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string RepoId { get; set; } = string.Empty;

    public DateTimeOffset DueAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class RegistryV2MirrorActionJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string RepoId { get; set; } = string.Empty;

    public string SourceImage { get; set; } = string.Empty;

    public string CommitSha { get; set; } = string.Empty;

    public string CommitUrl { get; set; } = string.Empty;

    public long? WorkflowRunId { get; set; }

    public string? WorkflowStatus { get; set; }

    public string? WorkflowConclusion { get; set; }

    public string? WorkflowUrl { get; set; }

    public DateTimeOffset? WorkflowCheckedAt { get; set; }

    public DateTimeOffset NextActionCheckAt { get; set; } = DateTimeOffset.Now;

    public int AcrProbeAttempts { get; set; }

    public DateTimeOffset? NextAcrProbeAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset? FailedAt { get; set; }

    public string? FailureMessage { get; set; }
}
