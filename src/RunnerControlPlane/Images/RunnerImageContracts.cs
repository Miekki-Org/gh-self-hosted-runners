namespace RunnerControlPlane.Images;

public enum RunnerImageBuildStatus
{
    Queued,
    Building,
    Validating,
    Complete,
    Failed
}

public sealed record RunnerImageBuildRequest(
    string UbuntuRelease,
    IReadOnlyList<string> DotNetSdks,
    IReadOnlyDictionary<string, string> Packages,
    string RunnerVersion,
    bool Expedited = false);

public sealed record RunnerImageInventory(
    string UbuntuRelease,
    IReadOnlyList<string> DotNetSdks,
    IReadOnlyDictionary<string, string> Packages,
    string RunnerVersion);

public sealed record RunnerImageValidationResult(
    bool ImageChecksPassed,
    bool VmBooted,
    bool GitHubRunnerRegistered,
    bool SampleDotNetBuildPassed,
    string? FailureReason = null)
{
    public bool IsSuccessful =>
        ImageChecksPassed && VmBooted && GitHubRunnerRegistered && SampleDotNetBuildPassed;
}

public sealed record RunnerImage(
    string Version,
    DateTimeOffset RequestedAt,
    bool Expedited,
    RunnerImageBuildRequest BuildRequest,
    RunnerImageBuildStatus Status,
    RunnerImageInventory? Inventory,
    string? FailureReason);

public sealed record RunnerImageArtifact(RunnerImageInventory Inventory);

public interface IRunnerImageBuilder
{
    Task<RunnerImageArtifact> BuildAsync(
        string version,
        RunnerImageBuildRequest request,
        CancellationToken cancellationToken);
}

public interface IRunnerImageValidator
{
    Task<RunnerImageValidationResult> ValidateAsync(
        RunnerImageArtifact image,
        CancellationToken cancellationToken);
}

public interface IRunnerImageStore
{
    IReadOnlyList<RunnerImage> List();
    IReadOnlyList<RunnerImage> ListAvailable();
    RunnerImage? Get(string version);
    RunnerImage AddQueued(string version, RunnerImageBuildRequest request);
    void Update(string version, Func<RunnerImage, RunnerImage> update);
}

public interface IRunnerImageBuildQueue
{
    RunnerImage Queue(RunnerImageBuildRequest request);
}
