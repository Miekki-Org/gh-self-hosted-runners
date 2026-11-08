using System.Collections.ObjectModel;

namespace RunnerControlPlane.Images;

public sealed class RunnerImageBuildQueue(
    IRunnerImageStore store,
    IRunnerImageBuilder builder,
    IRunnerImageValidator validator,
    ILogger<RunnerImageBuildQueue> logger)
    : BackgroundService, IRunnerImageBuildQueue
{
    private readonly object _queueLock = new();
    private readonly PriorityQueue<QueuedBuild, int> _queue = new();
    private readonly SemaphoreSlim _buildAvailable = new(0);

    public RunnerImage Queue(RunnerImageBuildRequest request)
    {
        var frozenRequest = Freeze(request);
        var version = $"ubuntu-{frozenRequest.UbuntuRelease}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        var image = store.AddQueued(version, frozenRequest);
        lock (_queueLock)
        {
            _queue.Enqueue(new QueuedBuild(version, frozenRequest), frozenRequest.Expedited ? 0 : 1);
            _buildAvailable.Release();
        }

        return image;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await _buildAvailable.WaitAsync(stoppingToken);
            QueuedBuild build;
            lock (_queueLock)
            {
                build = _queue.Dequeue();
            }

            await BuildAsync(build, stoppingToken);
        }
    }

    private async Task BuildAsync(QueuedBuild build, CancellationToken cancellationToken)
    {
        try
        {
            store.Update(build.Version, image => image with { Status = RunnerImageBuildStatus.Building });
            var artifact = await builder.BuildAsync(build.Version, build.Request, cancellationToken);
            var inventory = Freeze(artifact.Inventory);
            var validatedArtifact = new RunnerImageArtifact(inventory);
            store.Update(build.Version, image => image with
            {
                Status = RunnerImageBuildStatus.Validating,
                Inventory = inventory
            });

            var inventoryFailure = ValidateInventory(build.Request, inventory);
            if (inventoryFailure is not null)
            {
                store.Update(build.Version, image => image with
                {
                    Status = RunnerImageBuildStatus.Failed,
                    FailureReason = inventoryFailure
                });
                return;
            }

            var validation = await validator.ValidateAsync(validatedArtifact, cancellationToken);
            if (!validation.IsSuccessful)
            {
                var reason = string.IsNullOrWhiteSpace(validation.FailureReason)
                    ? "The image failed one or more required validation checks."
                    : validation.FailureReason;
                store.Update(build.Version, image => image with
                {
                    Status = RunnerImageBuildStatus.Failed,
                    FailureReason = reason
                });
                return;
            }

            store.Update(build.Version, image => image with
            {
                Status = RunnerImageBuildStatus.Complete,
                FailureReason = null
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Runner image build {Version} failed.", build.Version);
            store.Update(build.Version, image => image with
            {
                Status = RunnerImageBuildStatus.Failed,
                FailureReason = string.IsNullOrWhiteSpace(exception.Message)
                    ? exception.GetType().Name
                    : exception.Message
            });
        }
    }

    private static string? ValidateInventory(
        RunnerImageBuildRequest request,
        RunnerImageInventory inventory)
    {
        if (!inventory.UbuntuRelease.Equals(request.UbuntuRelease, StringComparison.OrdinalIgnoreCase)
            || !inventory.RunnerVersion.Equals(request.RunnerVersion, StringComparison.OrdinalIgnoreCase))
        {
            return "The built image did not match the pinned Ubuntu or GitHub Actions Runner version.";
        }

        var requestedSdks = new HashSet<string>(request.DotNetSdks, StringComparer.OrdinalIgnoreCase);
        var installedSdks = new HashSet<string>(inventory.DotNetSdks, StringComparer.OrdinalIgnoreCase);
        if (!requestedSdks.SetEquals(installedSdks))
        {
            return "The built image did not contain exactly the pinned .NET SDK versions.";
        }

        foreach (var package in request.Packages)
        {
            if (!inventory.Packages.TryGetValue(package.Key, out var installedVersion)
                || !installedVersion.Equals(package.Value, StringComparison.OrdinalIgnoreCase))
            {
                return $"The built image did not contain the pinned version of package '{package.Key}'.";
            }
        }

        return null;
    }

    private static RunnerImageInventory Freeze(RunnerImageInventory inventory)
    {
        return inventory with
        {
            DotNetSdks = Array.AsReadOnly(inventory.DotNetSdks.ToArray()),
            Packages = new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(inventory.Packages, StringComparer.OrdinalIgnoreCase))
        };
    }

    private static RunnerImageBuildRequest Freeze(RunnerImageBuildRequest request)
    {
        return request with
        {
            DotNetSdks = Array.AsReadOnly(request.DotNetSdks.ToArray()),
            Packages = new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(request.Packages, StringComparer.OrdinalIgnoreCase))
        };
    }

    private sealed record QueuedBuild(string Version, RunnerImageBuildRequest Request);
}
