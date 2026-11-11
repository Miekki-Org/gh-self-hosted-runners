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

public enum RunnerImageRolloutStage
{
    Pending,
    Canary,
    Promoting,
    Completed,
    Failed,
    RolledBack
}

public sealed record RunnerImageRolloutRequest(string ImageVersion, string? PreviousImageVersion = null);

public sealed record RunnerImageRolloutFailureRequest(string? FailureReason);

public sealed record RunnerImageRolloutAuditRecord(
    string Actor,
    DateTimeOffset Timestamp,
    string Action,
    string Outcome);

public sealed record RunnerImageRollout(
    string Id,
    string ImageVersion,
    string? PreviousImageVersion,
    RunnerImageRolloutStage Stage,
    string Actor,
    DateTimeOffset RequestedAt,
    DateTimeOffset? UpdatedAt,
    string? FailureReason = null,
    string? CurrentDeployedImageVersion = null,
    IReadOnlyList<RunnerImageRolloutAuditRecord>? AuditTrail = null)
{
    public string Status => Stage.ToString();
}

public interface IRunnerImageRolloutStore
{
    IReadOnlyList<RunnerImageRollout> List();
    RunnerImageRollout? Get(string id);
    RunnerImageRollout Create(string imageVersion, string? previousImageVersion, string actor);
    RunnerImageRollout Update(string id, Func<RunnerImageRollout, RunnerImageRollout> update);
    IReadOnlyList<RunnerImageRolloutAuditRecord> GetAuditTrail();
}

public interface IRunnerImageRolloutService
{
    Task<RunnerImageRollout> StartAsync(string imageVersion, string actor, CancellationToken cancellationToken);
    Task<RunnerImageRollout> MarkCanaryFailureAsync(string id, string failureReason, string actor, CancellationToken cancellationToken);
    Task<RunnerImageRollout> MarkCanarySuccessAsync(string id, string actor, CancellationToken cancellationToken);
}
