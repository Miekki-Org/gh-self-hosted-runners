using RunnerControlPlane.RunnerCapacity;

namespace RunnerControlPlane.Tests;

internal sealed class TestGitHubRunnerCapacityClient : IGitHubRunnerCapacityClient
{
    public Task<RunnerDemand> GetDemandAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(new RunnerDemand(0, 0));
    }
}
