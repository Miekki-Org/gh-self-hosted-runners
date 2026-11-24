namespace RunnerControlPlane.Configuration;

public sealed class ConditionalHostedService<T>(
    IServiceProvider services,
    IConfiguration configuration) : IHostedService where T : class, IHostedService
{
    private T? worker;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue("BackgroundWorkers:Enabled", true))
            return Task.CompletedTask;

        worker = services.GetRequiredService<T>();
        return worker.StartAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        worker?.StopAsync(cancellationToken) ?? Task.CompletedTask;
}
