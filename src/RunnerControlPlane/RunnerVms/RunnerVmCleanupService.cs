using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RunnerControlPlane.RunnerCapacity;

namespace RunnerControlPlane.RunnerVms;

public interface IRunnerVmCleanupService
{
    Task<int> RetireIdleVmsAsync(CancellationToken cancellationToken = default);
}

public sealed class RunnerVmCleanupService(
    IRunnerVmStore runnerVms,
    IOptions<RunnerCapacityOptions> options) : IRunnerVmCleanupService
{
    public Task<int> RetireIdleVmsAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<int>(cancellationToken);
        }

        var now = DateTimeOffset.UtcNow;
        var minimumCapacity = options.Value.MinimumCapacity;
        var gracePeriod = TimeSpan.FromSeconds(Math.Max(0, options.Value.IdleGracePeriodSeconds));
        var idleVms = runnerVms.List()
            .Where(vm => vm.Status == RunnerVmStatus.Idle && !vm.IsRetired && !vm.IsUnhealthy)
            .Where(vm => vm.UpdatedAt is not null && vm.UpdatedAt.Value.Add(gracePeriod) <= now)
            .OrderBy(vm => vm.UpdatedAt ?? DateTimeOffset.MinValue)
            .ToArray();

        if (idleVms.Length == 0)
        {
            return Task.FromResult(0);
        }

        var retired = 0;
        var eligibleIdleCount = runnerVms.List()
            .Count(vm => vm.Status == RunnerVmStatus.Idle && !vm.IsRetired && !vm.IsUnhealthy);
        foreach (var vm in idleVms)
        {
            if (eligibleIdleCount <= minimumCapacity)
            {
                break;
            }

            runnerVms.Retire(vm.Id, "Success", "system", null, "automatic-idle-retire");
            eligibleIdleCount--;
            retired++;
        }

        return Task.FromResult(retired);
    }
}

public sealed class RunnerVmCleanupWorker(
    IRunnerVmCleanupService cleanup,
    IOptions<RunnerCapacityOptions> options,
    ILogger<RunnerVmCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = Math.Max(1, options.Value.ReconcileIntervalSeconds);
        var delay = TimeSpan.FromSeconds(intervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await cleanup.RetireIdleVmsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Idle runner VM cleanup failed.");
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
