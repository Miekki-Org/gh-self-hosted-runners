namespace RunnerControlPlane.Configuration;

public sealed class EntraOptions
{
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string ReadOnlyGroupId { get; set; } = "";
    public string OperatorGroupId { get; set; } = "";
    public string ApiAudience { get; set; } = "";
}
