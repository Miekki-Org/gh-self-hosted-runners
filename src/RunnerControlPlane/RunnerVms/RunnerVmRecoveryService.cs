using Microsoft.Extensions.Options;
using RunnerControlPlane.Configuration;
using RunnerControlPlane.RunnerCapacity;

namespace RunnerControlPlane.RunnerVms;

public interface IRunnerVmRecoveryService
{
    Task<RunnerVmRecord?> RecordHealthCheckAsync(
        string id,
        RunnerVmHealthCheckRequest request,
        CancellationToken cancellationToken);

    Task<RunnerVmRecord?> UpdateAsync(
        string id,
        UpdateRunnerVmStatusRequest request,
        CancellationToken cancellationToken);

    Task<RunnerVmRecord?> ForceRemoveAsync(
        string id,
        string actor,
        CancellationToken cancellationToken);
}

public sealed class RunnerVmRecoveryService(
    IRunnerVmStore runnerVms,
    IRunnerCapacityController capacity,
    IOptions<RunnerVmOptions> options) : IRunnerVmRecoveryService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly int _healthFailureThreshold = options.Value.HealthFailureThreshold;

    public async Task<RunnerVmRecord?> RecordHealthCheckAsync(
        string id,
        RunnerVmHealthCheckRequest request,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = runnerVms.Get(id);
            if (current is null)
            {
                return null;
            }

            if (current.IsRetired)
            {
                return current;
            }

            var updated = runnerVms.Update(id, vm =>
            {
                if (vm.IsUnhealthy)
                {
                    return vm;
                }

                var vmFailures = request.VmHealthSucceeded ? 0 : vm.VmHealthFailures + 1;
                var connectivityFailures = request.RunnerConnectivitySucceeded
                    ? 0
                    : vm.RunnerConnectivityFailures + 1;
                var vmReason = request.VmHealthSucceeded
                    ? null
                    : string.IsNullOrWhiteSpace(request.VmHealthFailureReason)
                        ? "VM health check failed."
                        : request.VmHealthFailureReason;
                var connectivityReason = request.RunnerConnectivitySucceeded
                    ? null
                    : string.IsNullOrWhiteSpace(request.RunnerConnectivityFailureReason)
                        ? "GitHub runner connectivity check failed."
                        : request.RunnerConnectivityFailureReason;
                var isUnhealthy = vm.IsUnhealthy
                    || (vmFailures >= _healthFailureThreshold
                        && connectivityFailures >= _healthFailureThreshold);
                var reasons = new[] { vmReason, connectivityReason }
                    .Where(reason => reason is not null);

                return vm with
                {
                    VmHealthFailures = vmFailures,
                    RunnerConnectivityFailures = connectivityFailures,
                    VmHealthFailureReason = vmReason,
                    RunnerConnectivityFailureReason = connectivityReason,
                    HealthFailureReason = string.Join(" ", reasons),
                    IsUnhealthy = isUnhealthy,
                    AcceptingJobs = vm.AcceptingJobs && !isUnhealthy,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
            });

            if (updated.IsUnhealthy
                && updated.Status is RunnerVmStatus.Idle or RunnerVmStatus.Finished
                && !updated.IsRetired)
            {
                return await RetireAndReplaceAsync(updated, "system", "automatic-unhealthy-retire", cancellationToken);
            }

            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RunnerVmRecord?> UpdateAsync(
        string id,
        UpdateRunnerVmStatusRequest request,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var existing = runnerVms.Get(id);
            if (existing is null)
            {
                return null;
            }

            if (request.Status == RunnerVmStatus.Working && !existing.AcceptingJobs)
            {
                throw new RunnerVmNotAcceptingJobsException();
            }

            var updated = runnerVms.Update(id, current => current with
            {
                Status = request.Status,
                JobStatus = request.Status.ToString().ToLowerInvariant(),
                Outcome = request.Outcome ?? current.Outcome,
                FailureReason = request.Error ?? current.FailureReason,
                IsRetired = request.IsRetired ?? current.IsRetired,
                RetryScheduled = request.RetryScheduled ?? current.RetryScheduled,
                AcceptingJobs = current.AcceptingJobs
                    && request.Status != RunnerVmStatus.Finished
                    && !current.IsUnhealthy
                    && !(request.IsRetired ?? current.IsRetired),
                UpdatedAt = DateTimeOffset.UtcNow
            });

            if (updated.IsUnhealthy
                && updated.Status is RunnerVmStatus.Idle or RunnerVmStatus.Finished
                && !updated.IsRetired)
            {
                return await RetireAndReplaceAsync(updated, "system", "automatic-unhealthy-retire", cancellationToken);
            }

            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RunnerVmRecord?> ForceRemoveAsync(
        string id,
        string actor,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var vm = runnerVms.Get(id);
            if (vm is null || vm.IsRetired)
            {
                return vm;
            }

            string outcome;
            string? message = vm.HealthFailureReason;
            try
            {
                var scaleOut = await capacity.RequestScaleOutAsync(1, actor, cancellationToken);
                outcome = scaleOut.AddedCount == 0 ? "replacement-capped" : "Success";
                if (scaleOut.AddedCount > 0)
                {
                    runnerVms.Create(vm.ImageVersion, vm.Label, vm.RunnerGroupName);
                }
            }
            catch (RunnerCapacityIntegrationException exception)
            {
                outcome = "Failed";
                message = string.IsNullOrWhiteSpace(message)
                    ? exception.Message
                    : $"{message} Replacement request failed: {exception.Message}";
                runnerVms.Retire(id, outcome, actor, message, "force-remove");
                throw;
            }

            return runnerVms.Retire(id, outcome, actor, message, "force-remove");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RunnerVmRecord> RetireAndReplaceAsync(
        RunnerVmRecord vm,
        string actor,
        string action,
        CancellationToken cancellationToken)
    {
        var scaleOut = await capacity.RequestScaleOutAsync(1, actor, cancellationToken);
        var outcome = scaleOut.AddedCount == 0 ? "replacement-capped" : "Success";
        if (scaleOut.AddedCount > 0)
        {
            runnerVms.Create(vm.ImageVersion, vm.Label, vm.RunnerGroupName);
        }

        return runnerVms.Retire(vm.Id, outcome, actor, vm.HealthFailureReason, action);
    }
}
