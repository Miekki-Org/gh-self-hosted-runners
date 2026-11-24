using RunnerControlPlane.RunnerVms;

namespace RunnerControlPlane.Images;

public sealed class RunnerImageRolloutService : IRunnerImageRolloutService
{
    private readonly IRunnerImageStore _images;
    private readonly IRunnerVmStore _runnerVms;
    private readonly IRunnerImageRolloutStore _rollouts;
    private readonly IRunnerVmOperationLock _operationLock;

    public RunnerImageRolloutService(
        IRunnerImageStore images,
        IRunnerVmStore runnerVms,
        IRunnerImageRolloutStore rollouts,
        IRunnerVmOperationLock operationLock)
    {
        _images = images;
        _runnerVms = runnerVms;
        _rollouts = rollouts;
        _operationLock = operationLock;
    }

    public async Task<RunnerImageRollout> StartAsync(string imageVersion, string actor, CancellationToken cancellationToken)
    {
        var image = _images.ListAvailable()
            .FirstOrDefault(candidate => string.Equals(candidate.Version, imageVersion, StringComparison.Ordinal));
        if (image is null)
        {
            throw new InvalidOperationException($"Runner image '{imageVersion}' is not available for rollout.");
        }

        var lease = await _operationLock.AcquireAsync(cancellationToken);
        var mutationStarted = false;
        try
        {
            var current = _runnerVms.List()
                .Select(vm => vm.ImageVersion)
                .Where(version => !string.IsNullOrWhiteSpace(version))
                .Distinct(StringComparer.Ordinal)
                .FirstOrDefault() ?? image.Version;

            mutationStarted = true;
            var rollout = _rollouts.Create(imageVersion, current, actor);
            var result = _rollouts.Update(rollout.Id, currentRollout => currentRollout with
            {
                Stage = RunnerImageRolloutStage.Canary,
                UpdatedAt = DateTimeOffset.UtcNow,
                CurrentDeployedImageVersion = current,
                AuditTrail = AppendAudit(currentRollout.AuditTrail ?? [], actor, "canary-started", "canary")
            });
            lease.Complete();
            return result;
        }
        catch
        {
            if (!mutationStarted) lease.Complete();
            throw;
        }
    }

    public async Task<RunnerImageRollout> MarkCanaryFailureAsync(string id, string failureReason, string actor, CancellationToken cancellationToken)
    {
        var lease = await _operationLock.AcquireAsync(cancellationToken);
        var mutationStarted = false;
        try
        {
            var rollout = _rollouts.Get(id);
            if (rollout is null)
            {
                throw new KeyNotFoundException($"Runner image rollout '{id}' was not found.");
            }
            var previous = rollout.PreviousImageVersion ?? _runnerVms.List()
                .Select(vm => vm.ImageVersion)
                .FirstOrDefault(version => !string.IsNullOrWhiteSpace(version))
                ?? rollout.ImageVersion;

            foreach (var vm in _runnerVms.List())
            {
                if (string.Equals(vm.ImageVersion, rollout.ImageVersion, StringComparison.Ordinal))
                {
                    mutationStarted = true;
                    _runnerVms.Update(vm.Id, record =>
                        !string.Equals(record.ImageVersion, rollout.ImageVersion, StringComparison.Ordinal)
                            ? record
                            : record with
                            {
                                ImageVersion = previous,
                                UpdatedAt = DateTimeOffset.UtcNow,
                                FailureReason = failureReason,
                                Status = RunnerVmStatus.Idle,
                                AcceptingJobs = true
                            });
                }
            }

            mutationStarted = true;
            var result = _rollouts.Update(id, currentRollout => currentRollout with
            {
                Stage = RunnerImageRolloutStage.Failed,
                FailureReason = failureReason,
                CurrentDeployedImageVersion = previous,
                UpdatedAt = DateTimeOffset.UtcNow,
                AuditTrail = AppendAudit(currentRollout.AuditTrail ?? [], actor, "canary-failed", "failed")
            });
            lease.Complete();
            return result;
        }
        catch
        {
            if (!mutationStarted) lease.Complete();
            throw;
        }
    }

    public async Task<RunnerImageRollout> MarkCanarySuccessAsync(string id, string actor, CancellationToken cancellationToken)
    {
        var lease = await _operationLock.AcquireAsync(cancellationToken);
        var mutationStarted = false;
        try
        {
            var rollout = _rollouts.Get(id);
            if (rollout is null)
            {
                throw new KeyNotFoundException($"Runner image rollout '{id}' was not found.");
            }
            foreach (var vm in _runnerVms.List())
            {
                if (!string.Equals(vm.ImageVersion, rollout.ImageVersion, StringComparison.Ordinal))
                {
                    mutationStarted = true;
                    _runnerVms.Update(vm.Id, record => record with
                    {
                        ImageVersion = rollout.ImageVersion,
                        UpdatedAt = DateTimeOffset.UtcNow,
                        FailureReason = null,
                        AcceptingJobs = true
                    });
                }
            }

            mutationStarted = true;
            var result = _rollouts.Update(id, currentRollout => currentRollout with
            {
                Stage = RunnerImageRolloutStage.Completed,
                CurrentDeployedImageVersion = currentRollout.ImageVersion,
                UpdatedAt = DateTimeOffset.UtcNow,
                FailureReason = null,
                AuditTrail = AppendAudit(currentRollout.AuditTrail ?? [], actor, "canary-succeeded", "succeeded")
            });
            lease.Complete();
            return result;
        }
        catch
        {
            if (!mutationStarted) lease.Complete();
            throw;
        }
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
