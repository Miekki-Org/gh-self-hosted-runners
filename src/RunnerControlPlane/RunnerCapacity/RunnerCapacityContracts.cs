namespace RunnerControlPlane.RunnerCapacity;

public sealed class RunnerCapacityOptions
{
    public int MinimumCapacity { get; set; }

    public int MaximumCapacity { get; set; } = 10;

    public int ReconcileIntervalSeconds { get; set; } = 30;

    public string StateFilePath { get; set; } = "";
}

public sealed record RunnerDemand(int QueuedJobs, int BusyRunners)
{
    public IReadOnlyList<string> BusyRunnerNames { get; init; } = [];
}

public sealed record CapacitySnapshot(
    int MinimumCapacity,
    int MaximumCapacity,
    int CurrentCapacity,
    int BusyRunners,
    int IdleCapacity,
    int QueuedJobs,
    int DesiredCapacity,
    int UnservedJobs,
    bool IsCapped);

public sealed record UpdateCapacitySettingsRequest(int MinimumCapacity, int MaximumCapacity);

public sealed record ScaleOutRequest(int Count);

public sealed record CapacityAuditRecord(
    string Actor,
    DateTimeOffset Timestamp,
    string Action,
    string Outcome);

public sealed record RunnerCapacityState(
    int MinimumCapacity,
    int MaximumCapacity,
    IReadOnlyList<CapacityAuditRecord> AuditTrail);

public sealed record ScaleOutResult(
    int RequestedCount,
    int AddedCount,
    bool IsCapped,
    CapacitySnapshot Capacity);

public sealed class RunnerCapacityIntegrationException(string message, Exception innerException)
    : Exception(message, innerException);

public interface IGitHubRunnerCapacityClient
{
    Task<RunnerDemand> GetDemandAsync(CancellationToken cancellationToken);
}

public interface IAzureVmScaleSetClient
{
    Task<int> GetCapacityAsync(CancellationToken cancellationToken);

    Task<int> SetCapacityAsync(
        int desiredCapacity,
        IReadOnlyList<string> busyRunnerNames,
        CancellationToken cancellationToken);
}

public interface IRunnerCapacityController
{
    Task<CapacitySnapshot> GetSnapshotAsync(CancellationToken cancellationToken);

    Task<CapacitySnapshot> UpdateSettingsAsync(
        int minimumCapacity,
        int maximumCapacity,
        string actor,
        CancellationToken cancellationToken);

    Task<ScaleOutResult> RequestScaleOutAsync(
        int count,
        string actor,
        CancellationToken cancellationToken);

    Task<CapacitySnapshot> ReconcileAsync(CancellationToken cancellationToken);

    IReadOnlyList<CapacityAuditRecord> GetAuditTrail();
}

public interface IRunnerCapacityStateStore
{
    RunnerCapacityState? Load();

    Task SaveAsync(RunnerCapacityState state, CancellationToken cancellationToken);
}
