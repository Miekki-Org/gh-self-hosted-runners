namespace RunnerControlPlane.RunnerVms;

public enum RunnerVmStatus
{
    Idle,
    Working,
    Finished
}

public sealed record CreateRunnerVmRequest
{
    public CreateRunnerVmRequest()
    {
    }

    public CreateRunnerVmRequest(string? imageVersion, string? label = null)
    {
        ImageVersion = imageVersion;
        Label = label;
    }

    public string? ImageVersion { get; init; }

    public string? RunnerImageVersion
    {
        get => ImageVersion;
        init => ImageVersion = value;
    }

    public string? Label { get; init; }

    public string? RunnerLabel
    {
        get => Label;
        init => Label = value;
    }
}

public sealed record RunnerVmRecord(
    string Id,
    string ImageVersion,
    string Label,
    RunnerVmStatus Status,
    string? JobStatus = null,
    string? JobId = null,
    string? Outcome = null,
    bool IsRetired = false,
    bool RetryScheduled = false,
    string? FailureReason = null,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? UpdatedAt = null,
    string? RunnerGroup = null,
    bool IsUnhealthy = false,
    bool AcceptingJobs = true,
    int VmHealthFailures = 0,
    int RunnerConnectivityFailures = 0,
    string? VmHealthFailureReason = null,
    string? RunnerConnectivityFailureReason = null,
    string? HealthFailureReason = null)
{
    public string VmId => Id;

    public string? RunnerVmId => Id;

    public string? Image => ImageVersion;

    public string? JobState => JobStatus ?? (Status switch
    {
        RunnerVmStatus.Idle => "idle",
        RunnerVmStatus.Working => "working",
        RunnerVmStatus.Finished => "finished",
        _ => null
    });

    public string RunnerGroupName => RunnerGroup ?? "Shared Linux";

    public string? StatusText => Status.ToString();

    public string? Result => Outcome;

    public string? Error => FailureReason;

    public bool Retired => IsRetired;

    public bool IsIdle => Status == RunnerVmStatus.Idle && !IsRetired;

    public string? RunnerLabel => Label;
}

public sealed record UpdateRunnerVmStatusRequest(
    RunnerVmStatus Status,
    string? Outcome = null,
    string? Error = null,
    bool? RetryScheduled = null,
    bool? IsRetired = null);

public sealed record RunnerVmHealthCheckRequest(
    bool VmHealthSucceeded,
    bool RunnerConnectivitySucceeded,
    string? VmHealthFailureReason = null,
    string? RunnerConnectivityFailureReason = null);

public sealed class RunnerVmNotAcceptingJobsException()
    : Exception("Runner VM is not accepting new work.");

public sealed record RunnerVmAuditRecord(
    string Actor,
    DateTimeOffset Timestamp,
    string Action,
    string Outcome);

public interface IRunnerVmStore
{
    IReadOnlyList<RunnerVmRecord> List();
    RunnerVmRecord? Get(string id);
    RunnerVmRecord Create(string? imageVersion, string label, string runnerGroup);
    RunnerVmRecord Update(string id, Func<RunnerVmRecord, RunnerVmRecord> update);
    RunnerVmRecord Retire(string id, string outcome, string? message = null);
    RunnerVmRecord Retire(string id, string outcome, string actor, string? message, string action);
    IReadOnlyList<RunnerVmAuditRecord> GetAuditTrail();
}
