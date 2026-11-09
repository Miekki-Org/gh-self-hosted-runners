using Microsoft.Extensions.Options;

namespace RunnerControlPlane.RunnerCapacity;

public sealed class RunnerCapacityController : IRunnerCapacityController
{
    private readonly IGitHubRunnerCapacityClient _github;
    private readonly IAzureVmScaleSetClient _scaleSet;
    private readonly IRunnerCapacityStateStore _stateStore;
    private readonly object _settingsLock = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<CapacityAuditRecord> _auditTrail = [];
    private int _minimumCapacity;
    private int _maximumCapacity;

    public RunnerCapacityController(
        IGitHubRunnerCapacityClient github,
        IAzureVmScaleSetClient scaleSet,
        IOptions<RunnerCapacityOptions> options,
        IRunnerCapacityStateStore stateStore)
    {
        _github = github;
        _scaleSet = scaleSet;
        _stateStore = stateStore;
        var savedState = stateStore.Load();
        _minimumCapacity = savedState?.MinimumCapacity ?? options.Value.MinimumCapacity;
        _maximumCapacity = savedState?.MaximumCapacity ?? options.Value.MaximumCapacity;
        if (savedState is not null)
        {
            _auditTrail.AddRange(savedState.AuditTrail);
        }
    }

    public async Task<CapacitySnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var demand = await _github.GetDemandAsync(cancellationToken);
        var currentCapacity = await _scaleSet.GetCapacityAsync(cancellationToken);
        var (minimumCapacity, maximumCapacity) = GetSettings();
        return CreateSnapshot(minimumCapacity, maximumCapacity, currentCapacity, demand);
    }

    public async Task<CapacitySnapshot> UpdateSettingsAsync(
        int minimumCapacity,
        int maximumCapacity,
        string actor,
        CancellationToken cancellationToken)
    {
        if (minimumCapacity < 0 || maximumCapacity < 1 || minimumCapacity > maximumCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumCapacity));
        }

        await _gate.WaitAsync(cancellationToken);
        var (oldMinimumCapacity, oldMaximumCapacity) = GetSettings();
        var succeeded = false;
        var auditIndex = -1;
        try
        {
            auditIndex = BeginAuditRecord(actor, "update-capacity-settings");
            await SaveStateAsync(cancellationToken);
            SetSettings(minimumCapacity, maximumCapacity);
            var snapshot = await ReconcileCoreAsync(cancellationToken);
            SetAuditOutcome(auditIndex, "succeeded");
            await SaveStateAsync(cancellationToken);
            succeeded = true;
            return snapshot;
        }
        finally
        {
            if (!succeeded)
            {
                SetSettings(oldMinimumCapacity, oldMaximumCapacity);
                if (auditIndex < 0)
                {
                    auditIndex = BeginAuditRecord(actor, "update-capacity-settings");
                }

                SetAuditOutcome(auditIndex, "failed");
                await SaveStateAsync(CancellationToken.None);
            }

            _gate.Release();
        }
    }

    public async Task<ScaleOutResult> RequestScaleOutAsync(
        int count,
        string actor,
        CancellationToken cancellationToken)
    {
        if (count < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        await _gate.WaitAsync(cancellationToken);
        var succeeded = false;
        var auditIndex = -1;
        try
        {
            auditIndex = BeginAuditRecord(actor, "request-scale-out");
            await SaveStateAsync(cancellationToken);
            var (minimumCapacity, maximumCapacity) = GetSettings();
            var currentCapacity = await _scaleSet.GetCapacityAsync(cancellationToken);
            var targetCapacity = currentCapacity > maximumCapacity
                ? currentCapacity
                : (int)Math.Min(maximumCapacity, (long)currentCapacity + count);
            var demand = await _github.GetDemandAsync(cancellationToken);
            if (targetCapacity > currentCapacity)
            {
                targetCapacity = await _scaleSet.SetCapacityAsync(
                    targetCapacity,
                    demand.BusyRunnerNames,
                    cancellationToken);
            }

            var addedCount = Math.Max(0, targetCapacity - currentCapacity);
            var snapshot = CreateSnapshot(minimumCapacity, maximumCapacity, targetCapacity, demand);
            var isCapped = addedCount < count || snapshot.IsCapped;
            SetAuditOutcome(
                auditIndex,
                $"requested {count}; added {addedCount}; {(isCapped ? "capped" : "succeeded")}");
            await SaveStateAsync(cancellationToken);
            succeeded = true;
            return new ScaleOutResult(count, addedCount, isCapped, snapshot);
        }
        finally
        {
            if (!succeeded)
            {
                if (auditIndex < 0)
                {
                    auditIndex = BeginAuditRecord(actor, "request-scale-out");
                }

                SetAuditOutcome(auditIndex, "failed");
                await SaveStateAsync(CancellationToken.None);
            }

            _gate.Release();
        }
    }

    public async Task<CapacitySnapshot> ReconcileAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReconcileCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<CapacityAuditRecord> GetAuditTrail()
    {
        lock (_auditTrail)
        {
            return _auditTrail.ToArray();
        }
    }

    private async Task<CapacitySnapshot> ReconcileCoreAsync(CancellationToken cancellationToken)
    {
        var demand = await _github.GetDemandAsync(cancellationToken);
        var currentCapacity = await _scaleSet.GetCapacityAsync(cancellationToken);
        var (minimumCapacity, maximumCapacity) = GetSettings();
        if (demand.BusyRunners <= maximumCapacity)
        {
            var workload = demand.QueuedJobs + demand.BusyRunners;
            var desiredCapacity = Math.Min(maximumCapacity, Math.Max(minimumCapacity, workload));
            if (desiredCapacity != currentCapacity)
            {
                currentCapacity = await _scaleSet.SetCapacityAsync(
                    desiredCapacity,
                    demand.BusyRunnerNames,
                    cancellationToken);
            }
        }

        return CreateSnapshot(minimumCapacity, maximumCapacity, currentCapacity, demand);
    }

    private (int MinimumCapacity, int MaximumCapacity) GetSettings()
    {
        lock (_settingsLock)
        {
            return (_minimumCapacity, _maximumCapacity);
        }
    }

    private void SetSettings(int minimumCapacity, int maximumCapacity)
    {
        lock (_settingsLock)
        {
            _minimumCapacity = minimumCapacity;
            _maximumCapacity = maximumCapacity;
        }
    }

    private int BeginAuditRecord(string actor, string action)
    {
        lock (_auditTrail)
        {
            _auditTrail.Add(new CapacityAuditRecord(actor, DateTimeOffset.UtcNow, action, "in-progress"));
            return _auditTrail.Count - 1;
        }
    }

    private void SetAuditOutcome(int index, string outcome)
    {
        lock (_auditTrail)
        {
            _auditTrail[index] = _auditTrail[index] with { Outcome = outcome };
        }
    }

    private async Task SaveStateAsync(CancellationToken cancellationToken)
    {
        var (minimumCapacity, maximumCapacity) = GetSettings();
        await _stateStore.SaveAsync(
            new RunnerCapacityState(minimumCapacity, maximumCapacity, GetAuditTrail()),
            cancellationToken);
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
