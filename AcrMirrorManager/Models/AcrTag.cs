namespace AcrMirrorManager.Models;

public sealed record AcrTag(
    string Tag,
    string Digest,
    long? SizeBytes,
    string Status,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt);
