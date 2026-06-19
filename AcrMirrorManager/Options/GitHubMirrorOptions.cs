namespace AcrMirrorManager.Options;

public sealed class GitHubMirrorOptions
{
    public string RepositoryUrl { get; set; } = "https://github.com/Kerwin1202/docker_image_pusher";

    public string Owner { get; set; } = string.Empty;

    public string Repository { get; set; } = string.Empty;

    public string Branch { get; set; } = "main";

    public string ImagesPath { get; set; } = "images.txt";

    public string WorkflowFile { get; set; } = "docker.yaml";

    public string Token { get; set; } = string.Empty;

    public bool CommentOthersByDefault { get; set; } = true;

    public bool TriggerWorkflowDispatch { get; set; }

    public string EffectiveOwner => string.IsNullOrWhiteSpace(Owner)
        ? ParseRepositoryUrl().Owner
        : Owner.Trim();

    public string EffectiveRepository => string.IsNullOrWhiteSpace(Repository)
        ? ParseRepositoryUrl().Repository
        : Repository.Trim();

    public string RepositorySlug => $"{EffectiveOwner}/{EffectiveRepository}";

    private (string Owner, string Repository) ParseRepositoryUrl()
    {
        var value = RepositoryUrl.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return (string.Empty, string.Empty);
        }

        if (value.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
        {
            value = value["git@github.com:".Length..];
        }
        else if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            value = uri.AbsolutePath.Trim('/');
        }

        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^4];
        }

        var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 2
            ? (parts[^2], parts[^1])
            : (string.Empty, string.Empty);
    }
}
