namespace AcrMirrorManager.Options;

public sealed class RegistryV2Options
{
    public string Registry { get; set; } = "registry.cn-shanghai.aliyuncs.com";

    public string Namespace { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public bool IncludeCommentedImages { get; set; } = true;

    public int ManifestProbeLimit { get; set; } = 50;

    public string CachePath { get; set; } = "App_Data/registry-v2-cache.json";

    public int[] PostSubmitRefreshMinutes { get; set; } = [3, 5, 10, 20];

    public int DailyMissingRefreshHour { get; set; }
}
