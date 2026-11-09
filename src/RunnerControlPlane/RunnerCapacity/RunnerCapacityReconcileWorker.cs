using Microsoft.Extensions.Options;

namespace RunnerControlPlane.RunnerCapacity;

public sealed class RunnerCapacityReconcileWorker(
    IRunnerCapacityController controller,
    IOptions<RunnerCapacityOptions> options,
    ILogger<RunnerCapacityReconcileWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(options.Value.ReconcileIntervalSeconds));

        do
        {
            try
            {
                await controller.ReconcileAsync(stoppingToken);
            }
            catch (RunnerCapacityIntegrationException exception)
            {
                logger.LogError(exception, "Runner capacity reconciliation failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
