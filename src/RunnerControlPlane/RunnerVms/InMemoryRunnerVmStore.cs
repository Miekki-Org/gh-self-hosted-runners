using System.Collections.Concurrent;

namespace RunnerControlPlane.RunnerVms;

public sealed class InMemoryRunnerVmStore : IRunnerVmStore
{
    private readonly ConcurrentDictionary<string, RunnerVmRecord> _runnerVms = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<RunnerVmAuditRecord> _auditTrail = new();

    public IReadOnlyList<RunnerVmRecord> List()
    {
        return _runnerVms.Values
            .OrderByDescending(vm => vm.CreatedAt ?? DateTimeOffset.MinValue)
            .ToArray();
    }

    public RunnerVmRecord? Get(string id)
    {
        return _runnerVms.TryGetValue(id, out var vm) ? vm : null;
    }

    public RunnerVmRecord Create(string? imageVersion, string label, string runnerGroup)
    {
        var id = $"vm-{Guid.NewGuid():N}";
        var entry = new RunnerVmRecord(
            Id: id,
            ImageVersion: imageVersion ?? "latest",
            Label: label,
            Status: RunnerVmStatus.Idle,
            JobStatus: "idle",
            JobId: $"job-{Guid.NewGuid():N}",
            Outcome: null,
            IsRetired: false,
            RetryScheduled: false,
            FailureReason: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            RunnerGroup: runnerGroup);

        if (!_runnerVms.TryAdd(id, entry))
        {
            throw new InvalidOperationException($"Runner VM '{id}' could not be created.");
        }

        return entry;
    }

    public RunnerVmRecord Update(string id, Func<RunnerVmRecord, RunnerVmRecord> update)
    {
        if (!_runnerVms.TryGetValue(id, out var existing))
        {
            throw new KeyNotFoundException($"Runner VM '{id}' was not found.");
        }

        var updated = update(existing);
        _runnerVms[id] = updated;
        return updated;
    }

    public RunnerVmRecord Retire(string id, string outcome, string? message = null)
    {
        return Retire(id, outcome, "system", message, "manual-retire");
    }

    public RunnerVmRecord Retire(string id, string outcome, string actor, string? message, string action)
    {
        var existing = _runnerVms.GetOrAdd(id, _ => throw new KeyNotFoundException($"Runner VM '{id}' was not found."));
        var normalizedOutcome = outcome ?? "Success";
        var updated = existing with
        {
            Status = RunnerVmStatus.Finished,
            JobStatus = "finished",
            Outcome = normalizedOutcome,
            IsRetired = true,
            RetryScheduled = false,
            FailureReason = message,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _runnerVms[id] = updated;
        _auditTrail.Enqueue(new RunnerVmAuditRecord(
            actor,
            DateTimeOffset.UtcNow,
            action,
            NormalizeAuditOutcome(normalizedOutcome, action)));
        return updated;
    }

    public IReadOnlyList<RunnerVmAuditRecord> GetAuditTrail()
    {
        return _auditTrail.ToArray();
    }

    private static string NormalizeAuditOutcome(string outcome, string action)
    {
        if (string.Equals(outcome, "Success", StringComparison.OrdinalIgnoreCase)
            || string.Equals(outcome, "Succeeded", StringComparison.OrdinalIgnoreCase))
        {
            return action == "automatic-idle-retire" ? "retired" : "succeeded";
        }

        if (string.Equals(outcome, "Failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(outcome, "Cancelled", StringComparison.OrdinalIgnoreCase))
        {
            return "failed";
        }

        return action == "automatic-idle-retire" ? "retired" : outcome.ToLowerInvariant();
    }
}
