using Microsoft.Extensions.Options;

namespace RunnerControlPlane.RunnerCapacity;

public sealed class RunnerCapacityController : IRunnerCapacityController
{
    private readonly IGitHubRunnerCapacityClient _github;
    private readonly IAzureVmScaleSetClient _scaleSet;
    private readonly IRunnerCapacityStateStore _stateStore;
    private readonly int _defaultMinimumCapacity;
    private readonly int _defaultMaximumCapacity;

    public RunnerCapacityController(
        IGitHubRunnerCapacityClient github,
        IAzureVmScaleSetClient scaleSet,
        IOptions<RunnerCapacityOptions> options,
        IRunnerCapacityStateStore stateStore)
    {
        _github = github;
        _scaleSet = scaleSet;
        _stateStore = stateStore;
        _defaultMinimumCapacity = options.Value.MinimumCapacity;
        _defaultMaximumCapacity = options.Value.MaximumCapacity;
    }

    public async Task<CapacitySnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var state = LoadState();
        var demand = await _github.GetDemandAsync(cancellationToken);
        var capacity = await _scaleSet.GetCapacityAsync(cancellationToken);
        return CreateSnapshot(state.MinimumCapacity, state.MaximumCapacity, capacity, demand);
    }

    public async Task<CapacitySnapshot> UpdateSettingsAsync(
        int minimumCapacity, int maximumCapacity, string actor, CancellationToken cancellationToken)
    {
        if (minimumCapacity < 0 || maximumCapacity < 1 || minimumCapacity > maximumCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumCapacity));
        }

        await using var lease = await AcquireAsync(actor, "update-capacity-settings", cancellationToken);
        CapacitySnapshot snapshot;
        var attemptedChange = false;
        try
        {
            snapshot = await ReconcileCoreAsync(minimumCapacity, maximumCapacity, () => attemptedChange = true, cancellationToken);
        }
        catch
        {
            if (attemptedChange)
                await lease.FailUncertainAsync(CancellationToken.None);
            else
                await lease.CompleteAsync(lease.State.MinimumCapacity, lease.State.MaximumCapacity, "failed", CancellationToken.None);
            throw;
        }

        await lease.CompleteAsync(minimumCapacity, maximumCapacity, "succeeded", cancellationToken);
        return snapshot;
    }

    public async Task<ScaleOutResult> RequestScaleOutAsync(int count, string actor, CancellationToken cancellationToken)
    {
        if (count < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        await using var lease = await AcquireAsync(actor, "request-scale-out", cancellationToken);
        var state = lease.State;
        ScaleOutResult result;
        var attemptedChange = false;
        try
        {
            var currentCapacity = await _scaleSet.GetCapacityAsync(cancellationToken);
            var targetCapacity = currentCapacity > state.MaximumCapacity
                ? currentCapacity
                : (int)Math.Min(state.MaximumCapacity, (long)currentCapacity + count);
            var demand = await _github.GetDemandAsync(cancellationToken);
            if (targetCapacity > currentCapacity)
            {
                attemptedChange = true;
                targetCapacity = await _scaleSet.SetCapacityAsync(targetCapacity, demand.BusyRunnerNames, cancellationToken);
            }

            var addedCount = Math.Max(0, targetCapacity - currentCapacity);
            var snapshot = CreateSnapshot(state.MinimumCapacity, state.MaximumCapacity, targetCapacity, demand);
            result = new ScaleOutResult(count, addedCount, addedCount < count || snapshot.IsCapped, snapshot);
        }
        catch
        {
            if (attemptedChange)
                await lease.FailUncertainAsync(CancellationToken.None);
            else
                await lease.CompleteAsync(state.MinimumCapacity, state.MaximumCapacity, "failed", CancellationToken.None);
            throw;
        }

        await lease.CompleteAsync(state.MinimumCapacity, state.MaximumCapacity,
            $"requested {count}; added {result.AddedCount}; {(result.IsCapped ? "capped" : "succeeded")}", cancellationToken);
        return result;
    }

    public async Task<CapacitySnapshot> ReconcileAsync(CancellationToken cancellationToken)
    {
        await using var lease = await AcquireAsync(null, null, cancellationToken);
        var state = lease.State;
        CapacitySnapshot snapshot;
        var attemptedChange = false;
        try
        {
            snapshot = await ReconcileCoreAsync(state.MinimumCapacity, state.MaximumCapacity, () => attemptedChange = true, cancellationToken);
        }
        catch
        {
            if (attemptedChange)
                await lease.FailUncertainAsync(CancellationToken.None);
            else
                await lease.CompleteAsync(state.MinimumCapacity, state.MaximumCapacity, null, CancellationToken.None);
            throw;
        }

        await lease.CompleteAsync(state.MinimumCapacity, state.MaximumCapacity, null, cancellationToken);
        return snapshot;
    }

    public IReadOnlyList<CapacityAuditRecord> GetAuditTrail() => LoadState().AuditTrail;

    private RunnerCapacityState LoadState() => _stateStore.Load()
        ?? new RunnerCapacityState(_defaultMinimumCapacity, _defaultMaximumCapacity, []);

    private Task<IRunnerCapacityStateLease> AcquireAsync(string? actor, string? action, CancellationToken cancellationToken) =>
        _stateStore.AcquireAsync(_defaultMinimumCapacity, _defaultMaximumCapacity,
            actor is null ? null : new CapacityAuditRecord(actor, DateTimeOffset.UtcNow, action!, "in-progress"),
            cancellationToken);

    private async Task<CapacitySnapshot> ReconcileCoreAsync(
        int minimumCapacity, int maximumCapacity, Action onChange, CancellationToken cancellationToken)
    {
        var demand = await _github.GetDemandAsync(cancellationToken);
        var currentCapacity = await _scaleSet.GetCapacityAsync(cancellationToken);
        if (demand.BusyRunners <= maximumCapacity)
        {
            var workload = demand.QueuedJobs + demand.BusyRunners;
            var desiredCapacity = Math.Min(maximumCapacity, Math.Max(minimumCapacity, workload));
            if (desiredCapacity != currentCapacity)
            {
                onChange();
                currentCapacity = await _scaleSet.SetCapacityAsync(desiredCapacity, demand.BusyRunnerNames, cancellationToken);
            }
        }

        return CreateSnapshot(minimumCapacity, maximumCapacity, currentCapacity, demand);
    }

    private static CapacitySnapshot CreateSnapshot(
        int minimumCapacity,
        int maximumCapacity,
        int currentCapacity,
        RunnerDemand demand)
    {
        var workload = demand.QueuedJobs + demand.BusyRunners;
        var desiredCapacity = Math.Min(maximumCapacity, Math.Max(minimumCapacity, workload));
        var queuedCapacity = Math.Max(0, maximumCapacity - demand.BusyRunners);
        var unservedJobs = Math.Max(0, demand.QueuedJobs - queuedCapacity);
        return new CapacitySnapshot(
            minimumCapacity,
            maximumCapacity,
            currentCapacity,
            demand.BusyRunners,
            Math.Max(0, currentCapacity - demand.BusyRunners),
            demand.QueuedJobs,
            desiredCapacity,
            unservedJobs,
            unservedJobs > 0 || currentCapacity > maximumCapacity || demand.BusyRunners > maximumCapacity);
    }
}
