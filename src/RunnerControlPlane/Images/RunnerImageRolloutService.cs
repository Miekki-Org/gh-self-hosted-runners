using RunnerControlPlane.RunnerVms;

namespace RunnerControlPlane.Images;

public sealed class RunnerImageRolloutService : IRunnerImageRolloutService
{
    private readonly IRunnerImageStore _images;
    private readonly IRunnerVmStore _runnerVms;
    private readonly IRunnerImageRolloutStore _rollouts;

    public RunnerImageRolloutService(
        IRunnerImageStore images,
        IRunnerVmStore runnerVms,
        IRunnerImageRolloutStore rollouts)
    {
        _images = images;
        _runnerVms = runnerVms;
        _rollouts = rollouts;
    }

    public Task<RunnerImageRollout> StartAsync(string imageVersion, string actor, CancellationToken cancellationToken)
    {
        var image = _images.ListAvailable()
            .FirstOrDefault(candidate => string.Equals(candidate.Version, imageVersion, StringComparison.Ordinal));
        if (image is null)
        {
            throw new InvalidOperationException($"Runner image '{imageVersion}' is not available for rollout.");
        }

        var current = _runnerVms.List()
            .Select(vm => vm.ImageVersion)
            .Where(version => !string.IsNullOrWhiteSpace(version))
            .Distinct(StringComparer.Ordinal)
            .FirstOrDefault() ?? image.Version;

        var rollout = _rollouts.Create(imageVersion, current, actor);
        var canary = rollout with
        {
            Stage = RunnerImageRolloutStage.Canary,
            UpdatedAt = DateTimeOffset.UtcNow,
            CurrentDeployedImageVersion = current,
            AuditTrail = AppendAudit(rollout.AuditTrail ?? [], actor, "canary-started", "canary")
        };

        return Task.FromResult(_rollouts.Update(rollout.Id, _ => canary));
    }

    public Task<RunnerImageRollout> MarkCanaryFailureAsync(string id, string failureReason, string actor, CancellationToken cancellationToken)
    {
        var rollout = _rollouts.Get(id) ?? throw new KeyNotFoundException($"Runner image rollout '{id}' was not found.");
        var previous = rollout.PreviousImageVersion ?? _runnerVms.List()
            .Select(vm => vm.ImageVersion)
            .FirstOrDefault(version => !string.IsNullOrWhiteSpace(version))
            ?? rollout.ImageVersion;

        foreach (var vm in _runnerVms.List())
        {
            if (string.Equals(vm.ImageVersion, rollout.ImageVersion, StringComparison.Ordinal))
            {
                _runnerVms.Update(vm.Id, record => record with
                {
                    ImageVersion = previous,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    FailureReason = failureReason,
                    Status = RunnerVmStatus.Idle,
                    AcceptingJobs = true
                });
            }
        }

        var failed = rollout with
        {
            Stage = RunnerImageRolloutStage.Failed,
            FailureReason = failureReason,
            CurrentDeployedImageVersion = previous,
            UpdatedAt = DateTimeOffset.UtcNow,
            AuditTrail = AppendAudit(rollout.AuditTrail ?? [], actor, "canary-failed", "failed")
        };

        return Task.FromResult(_rollouts.Update(id, _ => failed));
    }

    public Task<RunnerImageRollout> MarkCanarySuccessAsync(string id, string actor, CancellationToken cancellationToken)
    {
        var rollout = _rollouts.Get(id) ?? throw new KeyNotFoundException($"Runner image rollout '{id}' was not found.");
        foreach (var vm in _runnerVms.List())
        {
            if (!string.Equals(vm.ImageVersion, rollout.ImageVersion, StringComparison.Ordinal))
            {
                _runnerVms.Update(vm.Id, record => record with
                {
                    ImageVersion = rollout.ImageVersion,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    FailureReason = null,
                    AcceptingJobs = true
                });
            }
        }

        var completed = rollout with
        {
            Stage = RunnerImageRolloutStage.Completed,
            CurrentDeployedImageVersion = rollout.ImageVersion,
            UpdatedAt = DateTimeOffset.UtcNow,
            FailureReason = null,
            AuditTrail = AppendAudit(rollout.AuditTrail ?? [], actor, "canary-succeeded", "succeeded")
        };

        return Task.FromResult(_rollouts.Update(id, _ => completed));
    }

    private static IReadOnlyList<RunnerImageRolloutAuditRecord> AppendAudit(
        IReadOnlyList<RunnerImageRolloutAuditRecord> trail,
        string actor,
        string action,
        string outcome)
    {
        var next = trail.ToList();
        next.Add(new RunnerImageRolloutAuditRecord(
            actor,
            DateTimeOffset.UtcNow,
            action,
            outcome));
        return next;
    }
}
