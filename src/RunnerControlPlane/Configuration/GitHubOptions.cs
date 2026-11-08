namespace RunnerControlPlane.Configuration;

public sealed class GitHubOptions
{
    public string Organization { get; set; } = "";
    public string Token { get; set; } = "";
    public long RunnerGroupId { get; set; }
    public string ApiBaseUrl { get; set; } = "https://api.github.com/";
}
