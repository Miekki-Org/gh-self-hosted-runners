using System.Collections.Concurrent;

namespace RunnerControlPlane.Images;

public sealed class InMemoryRunnerImageRolloutStore : IRunnerImageRolloutStore
{
    private readonly ConcurrentDictionary<string, RunnerImageRollout> _rollouts = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<RunnerImageRolloutAuditRecord> _auditTrail = new();

    public IReadOnlyList<RunnerImageRollout> List()
    {
        return _rollouts.Values
            .OrderByDescending(rollout => rollout.RequestedAt)
            .ToArray();
    }

    public RunnerImageRollout? Get(string id)
    {
        return _rollouts.GetValueOrDefault(id);
    }

    public RunnerImageRollout Create(string imageVersion, string? previousImageVersion, string actor)
    {
        var id = $"rollout-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var audit = new RunnerImageRolloutAuditRecord(actor, now, "request-rollout", "queued");
        var rollout = new RunnerImageRollout(
            Id: id,
            ImageVersion: imageVersion,
            PreviousImageVersion: previousImageVersion,
            Stage: RunnerImageRolloutStage.Pending,
            Actor: actor,
            RequestedAt: now,
            UpdatedAt: now,
            FailureReason: null,
            CurrentDeployedImageVersion: previousImageVersion,
            AuditTrail: [audit]);

        if (!_rollouts.TryAdd(id, rollout))
        {
            throw new InvalidOperationException($"Runner image rollout '{id}' could not be created.");
        }

        _auditTrail.Enqueue(audit);
        return rollout;
    }

    public RunnerImageRollout Update(string id, Func<RunnerImageRollout, RunnerImageRollout> update)
    {
        if (!_rollouts.TryGetValue(id, out var existing))
        {
            throw new KeyNotFoundException($"Runner image rollout '{id}' was not found.");
        }

        var updated = update(existing);
        _rollouts[id] = updated;

        if (updated.AuditTrail is not null)
        {
            var previousCount = existing.AuditTrail?.Count ?? 0;
            foreach (var audit in updated.AuditTrail.Skip(previousCount))
            {
                _auditTrail.Enqueue(audit);
            }
        }

        return updated;
    }

    public IReadOnlyList<RunnerImageRolloutAuditRecord> GetAuditTrail()
    {
        return _auditTrail.ToArray();
    }
}
