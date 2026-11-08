namespace RunnerControlPlane.Images;

public sealed class RunnerImageBuildOptions
{
    public bool RoutineBuildEnabled { get; set; }
    public string RoutineBuildDayUtc { get; set; } = "Sunday";
    public string RoutineBuildTimeUtc { get; set; } = "03:00";
    public RunnerImageBuildRequestOptions? RoutineBuildRequest { get; set; }
}

public sealed class RunnerImageBuildRequestOptions
{
    public string UbuntuRelease { get; set; } = "";
    public List<string> DotNetSdks { get; set; } = [];
    public Dictionary<string, string> Packages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string RunnerVersion { get; set; } = "";

    public RunnerImageBuildRequest ToRequest()
    {
        return new RunnerImageBuildRequest(UbuntuRelease, DotNetSdks, Packages, RunnerVersion);
    }
}
