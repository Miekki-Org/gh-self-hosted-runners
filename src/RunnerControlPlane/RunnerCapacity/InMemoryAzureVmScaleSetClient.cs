using Microsoft.Extensions.Options;

namespace RunnerControlPlane.RunnerCapacity;

public sealed class InMemoryAzureVmScaleSetClient(IOptions<RunnerCapacityOptions> options)
    : IAzureVmScaleSetClient
{
    private int _capacity = options.Value.MinimumCapacity;

    public Task<int> GetCapacityAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(Volatile.Read(ref _capacity));
    }

    public Task<int> SetCapacityAsync(
        int desiredCapacity,
        IReadOnlyList<string> busyRunnerNames,
        CancellationToken cancellationToken)
    {
        if (desiredCapacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(desiredCapacity));
        }

        Interlocked.Exchange(ref _capacity, desiredCapacity);
        return Task.FromResult(desiredCapacity);
    }
}
