using System.Collections.Concurrent;

namespace RunnerControlPlane.RunnerVms;

public sealed class InMemoryRunnerVmStore : IRunnerVmStore
{
    private readonly ConcurrentDictionary<string, RunnerVmRecord> _runnerVms = new(StringComparer.Ordinal);

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
        var existing = _runnerVms.GetOrAdd(id, _ => throw new KeyNotFoundException($"Runner VM '{id}' was not found."));
        var updated = existing with
        {
            Status = RunnerVmStatus.Finished,
            JobStatus = "finished",
            Outcome = outcome,
            IsRetired = true,
            RetryScheduled = false,
            FailureReason = message,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _runnerVms[id] = updated;
        return updated;
    }
}
