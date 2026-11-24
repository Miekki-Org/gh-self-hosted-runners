using System.Text;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;

namespace RunnerControlPlane.Images;

public sealed class RunnerImageBuildQueue : BackgroundService, IRunnerImageBuildQueue
{
    private readonly IRunnerImageStore store;
    private readonly IRunnerImageBuilder builder;
    private readonly IRunnerImageValidator validator;
    private readonly ILogger<RunnerImageBuildQueue> logger;
    private readonly TableClient? table;
    private readonly object _queueLock = new();
    private readonly PriorityQueue<QueuedBuild, int> _queue = new();
    private readonly SemaphoreSlim _buildAvailable = new(0);

    public RunnerImageBuildQueue(
        IRunnerImageStore store, IRunnerImageBuilder builder, IRunnerImageValidator validator,
        ILogger<RunnerImageBuildQueue> logger)
    {
        this.store = store;
        this.builder = builder;
        this.validator = validator;
        this.logger = logger;
    }

    public RunnerImageBuildQueue(
        IRunnerImageStore store, IRunnerImageBuilder builder, IRunnerImageValidator validator,
        ILogger<RunnerImageBuildQueue> logger, TableClient table)
        : this(store, builder, validator, logger)
    {
        this.table = table ?? throw new ArgumentNullException(nameof(table));
    }

    public RunnerImage Queue(RunnerImageBuildRequest request)
    {
        var frozenRequest = RunnerImageSnapshot.Freeze(request);
        var version = $"ubuntu-{frozenRequest.UbuntuRelease}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        if (table is not null)
        {
            AddDurableBuild($"build-{Guid.NewGuid():N}", version, frozenRequest);
            return EnsureImage(version, frozenRequest);
        }

        var image = store.AddQueued(version, frozenRequest);
        lock (_queueLock)
        {
            _queue.Enqueue(new QueuedBuild(version, frozenRequest), frozenRequest.Expedited ? 0 : 1);
            _buildAvailable.Release();
        }

        return image;
    }

    // The weekly key and queued payload are one insert, so a crash cannot leave a
    // schedule marker without its job. A 409 means another replica won that week.
    public bool QueueRoutine(RunnerImageBuildRequest request, DateOnly week)
    {
        if (table is null)
        {
            Queue(request);
            return true;
        }

        var frozenRequest = RunnerImageSnapshot.Freeze(request);
        var version = $"ubuntu-{frozenRequest.UbuntuRelease}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        try
        {
            AddDurableBuild($"weekly-{week:yyyyMMdd}", version, frozenRequest);
        }
        catch (RequestFailedException exception) when (exception.Status == 409)
        {
            return false;
        }

        EnsureImage(version, frozenRequest);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (table is not null)
        {
            await ExecuteDurableAsync(stoppingToken);
            return;
        }

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

    private const string QueuePartition = "image-build-queue";
    private static readonly JsonSerializerOptions QueueJsonOptions = new(JsonSerializerDefaults.Web);

    private void AddDurableBuild(string key, string version, RunnerImageBuildRequest request)
    {
        var payload = JsonSerializer.Serialize(request, QueueJsonOptions);
        if (Encoding.Unicode.GetByteCount(payload) > 64 * 1024)
        {
            throw new InvalidDataException("The runner image build request exceeds the Azure Table string property limit.");
        }

        table!.AddEntity(new TableEntity(QueuePartition, key)
        {
            ["Version"] = version,
            ["Request"] = payload,
            ["State"] = "Pending",
            ["Expedited"] = request.Expedited,
            ["QueuedAt"] = DateTimeOffset.UtcNow
        });
    }

    private RunnerImage EnsureImage(string version, RunnerImageBuildRequest request)
    {
        var existing = store.Get(version);
        if (existing is not null)
        {
            return existing;
        }

        try
        {
            return store.AddQueued(version, request);
        }
        catch
        {
            // The producer and a worker can materialize the same durable job concurrently.
            existing = store.Get(version);
            if (existing is not null)
            {
                return existing;
            }
            throw;
        }
    }

    private async Task ExecuteDurableAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var pending = table!.Query<TableEntity>(
                        filter: "PartitionKey eq 'image-build-queue' and State eq 'Pending'",
                        cancellationToken: stoppingToken)
                    .OrderByDescending(row => row.GetBoolean("Expedited") == true)
                    .ThenBy(row => row.GetDateTimeOffset("QueuedAt"))
                    .ToArray();

                foreach (var row in pending)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    var version = row.GetString("Version") ?? throw new InvalidDataException("Build row has no version.");
                    var request = JsonSerializer.Deserialize<RunnerImageBuildRequest>(
                        row.GetString("Request") ?? throw new InvalidDataException("Build row has no request."),
                        QueueJsonOptions) ?? throw new InvalidDataException("Build row has an invalid request.");
                    EnsureImage(version, request);

                    // This permanent CAS claim is deliberately never leased or retried.
                    // A crash or uncertain claim response may have started an external build.
                    // Operators must investigate the builder and reconcile the image; only
                    // after confirming it is safe may they submit a NEW build. Never reset
                    // a claim or delete a weekly row to retry it.
                    row["State"] = "Claimed";
                    row["ClaimedAt"] = DateTimeOffset.UtcNow;
                    try
                    {
                        table.UpdateEntity(row, row.ETag, TableUpdateMode.Replace, stoppingToken);
                    }
                    catch (RequestFailedException exception) when (exception.Status is 404 or 412)
                    {
                        continue;
                    }

                    logger.LogInformation("Claimed durable runner image build {Version}.", version);
                    await BuildAsync(new QueuedBuild(version, request), stoppingToken);
                    // The claim is retained even if this bookkeeping write fails.
                    row["State"] = "Finished";
                    table.UpdateEntity(row, ETag.All, TableUpdateMode.Replace, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Could not process the durable runner image build queue; retrying.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    private async Task BuildAsync(QueuedBuild build, CancellationToken cancellationToken)
    {
        try
        {
            store.Update(build.Version, image => image with { Status = RunnerImageBuildStatus.Building });
            var artifact = await builder.BuildAsync(build.Version, build.Request, cancellationToken);
            var inventory = RunnerImageSnapshot.Freeze(artifact.Inventory);
            var validatedArtifact = new RunnerImageArtifact(inventory);
            store.Update(build.Version, image => image with
            {
                Status = RunnerImageBuildStatus.Validating,
                Inventory = inventory
            });

            var inventoryFailure = RunnerImageInventoryValidator.Validate(build.Request, inventory);
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

    private sealed record QueuedBuild(string Version, RunnerImageBuildRequest Request);
}
