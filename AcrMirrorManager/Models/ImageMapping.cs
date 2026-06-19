namespace AcrMirrorManager.Models;

public sealed record ImageMapping(
    string SourceLine,
    string RepositoryName,
    string Tag);
