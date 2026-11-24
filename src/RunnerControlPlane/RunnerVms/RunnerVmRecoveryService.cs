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
    IOptions<RunnerVmOptions> options,
    IRunnerVmOperationLock operationLock) : IRunnerVmRecoveryService
{
    private readonly int _healthFailureThreshold = options.Value.HealthFailureThreshold;

    public async Task<RunnerVmRecord?> RecordHealthCheckAsync(
        string id,
        RunnerVmHealthCheckRequest request,
        CancellationToken cancellationToken)
    {
        var lease = await operationLock.AcquireAsync(cancellationToken);
        var completed = false;
        var mutationStarted = false;
        try
        {
            var current = runnerVms.Get(id);
            if (current is null)
            {
                completed = true;
                return null;
            }

            if (current.IsRetired)
            {
                completed = true;
                return current;
            }

            mutationStarted = true;
            var updated = runnerVms.Update(id, vm =>
            {
                if (vm.IsUnhealthy || vm.IsRetired)
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
                var retired = await RetireAndReplaceAsync(updated, "system", "automatic-unhealthy-retire", cancellationToken);
                completed = true;
                return retired;
            }

            completed = true;
            return updated;
        }
        finally
        {
            if (completed || !mutationStarted) lease.Complete();
        }
    }

    public async Task<RunnerVmRecord?> UpdateAsync(
        string id,
        UpdateRunnerVmStatusRequest request,
        CancellationToken cancellationToken)
    {
        var lease = await operationLock.AcquireAsync(cancellationToken);
        var completed = false;
        var mutationStarted = false;
        try
        {
            var existing = runnerVms.Get(id);
            if (existing is null)
            {
                completed = true;
                return null;
            }

            if (request.Status == RunnerVmStatus.Working && !existing.AcceptingJobs)
            {
                completed = true;
                throw new RunnerVmNotAcceptingJobsException();
            }

            mutationStarted = true;
            var updated = runnerVms.Update(id, current =>
            {
                if (request.Status == RunnerVmStatus.Working && !current.AcceptingJobs)
                    throw new RunnerVmNotAcceptingJobsException();
                return current with
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
                };
            });

            if (updated.IsUnhealthy
                && updated.Status is RunnerVmStatus.Idle or RunnerVmStatus.Finished
                && !updated.IsRetired)
            {
                var retired = await RetireAndReplaceAsync(updated, "system", "automatic-unhealthy-retire", cancellationToken);
                completed = true;
                return retired;
            }

            completed = true;
            return updated;
        }
        catch (RunnerVmNotAcceptingJobsException)
        {
            completed = true;
            throw;
        }
        finally
        {
            if (completed || !mutationStarted) lease.Complete();
        }
    }

    public async Task<RunnerVmRecord?> ForceRemoveAsync(
        string id,
        string actor,
        CancellationToken cancellationToken)
    {
        var lease = await operationLock.AcquireAsync(cancellationToken);
        var completed = false;
        var mutationStarted = false;
        try
        {
            var vm = runnerVms.Get(id);
            if (vm is null || vm.IsRetired)
            {
                completed = true;
                return vm;
            }

            string outcome;
            string? message = vm.HealthFailureReason;
            try
            {
                mutationStarted = true;
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

            var retired = runnerVms.Retire(id, outcome, actor, message, "force-remove");
            completed = true;
            return retired;
        }
        finally
        {
            if (completed || !mutationStarted) lease.Complete();
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
