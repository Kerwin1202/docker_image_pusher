namespace AcrMirrorManager.Options;

public sealed class AliyunAcrOptions
{
    public string AccessKeyId { get; set; } = string.Empty;

    public string AccessKeySecret { get; set; } = string.Empty;

    public string RegionId { get; set; } = "cn-hangzhou";

    public string InstanceId { get; set; } = string.Empty;

    public string Namespace { get; set; } = string.Empty;

    public string Endpoint { get; set; } = string.Empty;

    public int PageSize { get; set; } = 100;
}
