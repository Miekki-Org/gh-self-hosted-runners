using Azure;
using Azure.Core;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RunnerControlPlane.Images;

namespace RunnerControlPlane.Tests;

public sealed class RunnerImageDurableQueueTests
{
    private static readonly RunnerImageBuildRequest Request = new(
        "24.04", ["8.0.100"], new Dictionary<string, string> { ["git"] = "2.0" }, "2.300.0");

    [Fact]
    public async Task PendingBuildSurvivesRestartAndIsClaimedByOnlyOneReplica()
    {
        var table = new FakeTable();
        var store = new AzureTableRunnerImageStore(table);
        var builder = new Builder();
        var producer = Queue(store, builder, table);
        var image = producer.Queue(Request);
        Assert.Equal(RunnerImageBuildStatus.Queued, image.Status);

        var first = Queue(store, builder, table);
        var second = Queue(store, builder, table);
        await Task.WhenAll(first.StartAsync(default), second.StartAsync(default));
        try
        {
            await WaitUntilAsync(() => store.Get(image.Version)?.Status == RunnerImageBuildStatus.Complete);
            Assert.Equal(1, builder.Count);
        }
        finally
        {
            await Task.WhenAll(first.StopAsync(default), second.StopAsync(default));
        }
    }

    [Fact]
    public async Task InterruptedClaimIsNotExecutedAgainAfterRestart()
    {
        var table = new FakeTable();
        var store = new AzureTableRunnerImageStore(table);
        var builder = new Builder { Block = true };
        var first = Queue(store, builder, table);
        var image = first.Queue(Request);
        await first.StartAsync(default);
        await builder.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await first.StopAsync(default);

        builder.Block = false;
        var restarted = Queue(store, builder, table);
        await restarted.StartAsync(default);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            Assert.Equal(1, builder.Count);
            Assert.Equal(RunnerImageBuildStatus.Building, store.Get(image.Version)!.Status);
        }
        finally
        {
            await restarted.StopAsync(default);
        }
    }

    [Fact]
    public async Task PendingJobIsRecoveredWhenImageInsertionFails()
    {
        var table = new FakeTable { FailNextImageInsert = true };
        var store = new AzureTableRunnerImageStore(table);
        var builder = new Builder();
        Assert.Throws<RequestFailedException>(() => Queue(store, builder, table).Queue(Request));

        var restarted = Queue(store, builder, table);
        await restarted.StartAsync(default);
        try
        {
            await WaitUntilAsync(() => store.List().Any(image => image.Status == RunnerImageBuildStatus.Complete));
            Assert.Equal(1, builder.Count);
        }
        finally
        {
            await restarted.StopAsync(default);
        }
    }

    [Fact]
    public async Task AmbiguousClaimResponseFailsClosed()
    {
        var table = new FakeTable { FailAfterNextClaim = true };
        var store = new AzureTableRunnerImageStore(table);
        var builder = new Builder();
        var first = Queue(store, builder, table);
        var image = first.Queue(Request);
        await first.StartAsync(default);
        await table.AmbiguousClaim.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await first.StopAsync(default);

        var restarted = Queue(store, builder, table);
        await restarted.StartAsync(default);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            Assert.Equal(0, builder.Count);
            Assert.Equal(RunnerImageBuildStatus.Queued, store.Get(image.Version)!.Status);
        }
        finally
        {
            await restarted.StopAsync(default);
        }
    }

    [Fact]
    public async Task WeeklyJobsAreDeduplicatedAcrossReplicasAndRestarts()
    {
        var table = new FakeTable();
        var store = new AzureTableRunnerImageStore(table);
        var builder = new Builder();
        var week = new DateOnly(2026, 11, 15);
        var queues = Enumerable.Range(0, 8).Select(_ => Queue(store, builder, table)).ToArray();
        var results = await Task.WhenAll(queues.Select(queue => Task.Run(() => queue.QueueRoutine(Request, week))));
        Assert.Single(results, result => result);
        Assert.False(Queue(store, builder, table).QueueRoutine(Request, week));
        Assert.True(Queue(store, builder, table).QueueRoutine(Request, week.AddDays(7)));
        Assert.Equal(2, store.List().Count);
    }

    [Fact]
    public async Task SchedulersShareTheDurableWeeklyMarker()
    {
        var table = new FakeTable();
        var store = new AzureTableRunnerImageStore(table);
        var builder = new Builder();
        var settings = Options.Create(new RunnerImageBuildOptions
        {
            RoutineBuildEnabled = true,
            RoutineBuildDayUtc = DateTimeOffset.UtcNow.DayOfWeek.ToString(),
            RoutineBuildTimeUtc = "00:00",
            RoutineBuildRequest = new RunnerImageBuildRequestOptions
            {
                UbuntuRelease = Request.UbuntuRelease,
                DotNetSdks = Request.DotNetSdks.ToList(),
                Packages = new Dictionary<string, string>(Request.Packages),
                RunnerVersion = Request.RunnerVersion
            }
        });
        RunnerImageScheduleWorker Scheduler() => new(
            Queue(store, builder, table), settings, NullLogger<RunnerImageScheduleWorker>.Instance);
        var workers = Enumerable.Range(0, 4).Select(_ => Scheduler()).ToArray();
        await Task.WhenAll(workers.Select(worker => worker.StartAsync(default)));
        try
        {
            await WaitUntilAsync(() => store.List().Count == 1);
            await Task.Delay(200);
        }
        finally
        {
            await Task.WhenAll(workers.Select(worker => worker.StopAsync(default)));
        }

        var restarted = Scheduler();
        await restarted.StartAsync(default);
        try
        {
            await Task.Delay(200);
            Assert.Single(store.List());
        }
        finally
        {
            await restarted.StopAsync(default);
        }
    }

    private static RunnerImageBuildQueue Queue(IRunnerImageStore store, Builder builder, TableClient table) =>
        new(store, builder, new Validator(), NullLogger<RunnerImageBuildQueue>.Instance, table);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for image build.");
            await Task.Delay(25);
        }
    }

    private sealed class Builder : IRunnerImageBuilder
    {
        private int count;
        public int Count => Volatile.Read(ref count);
        public bool Block { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<RunnerImageArtifact> BuildAsync(
            string version, RunnerImageBuildRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref count);
            Started.TrySetResult();
            if (Block)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return new RunnerImageArtifact(new RunnerImageInventory(
                request.UbuntuRelease, request.DotNetSdks, request.Packages, request.RunnerVersion));
        }
    }

    private sealed class Validator : IRunnerImageValidator
    {
        public Task<RunnerImageValidationResult> ValidateAsync(RunnerImageArtifact image, CancellationToken cancellationToken) =>
            Task.FromResult(new RunnerImageValidationResult(true, true, true, true));
    }

    private sealed class FakeTable : TableClient
    {
        private readonly object gate = new();
        private readonly Dictionary<(string, string), TableEntity> rows = new();
        private int revision;
        public bool FailNextImageInsert { get; set; }
        public bool FailAfterNextClaim { get; set; }
        public TaskCompletionSource AmbiguousClaim { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Response AddEntity<T>(T entity, CancellationToken cancellationToken = default)
        {
            var row = (TableEntity)(object)entity;
            lock (gate)
            {
                if (row.PartitionKey == "runner-images" && FailNextImageInsert)
                {
                    FailNextImageInsert = false;
                    throw new RequestFailedException(503, "Unavailable");
                }
                if (rows.ContainsKey((row.PartitionKey, row.RowKey)))
                    throw new RequestFailedException(409, "Exists");
                rows.Add((row.PartitionKey, row.RowKey), Copy(row, new ETag((++revision).ToString())));
                return new FakeResponse();
            }
        }

        public override Response<T> GetEntity<T>(
            string partitionKey, string rowKey, IEnumerable<string>? select = null,
            CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                if (!rows.TryGetValue((partitionKey, rowKey), out var row))
                    throw new RequestFailedException(404, "Not found", "EntityNotFound", null);
                return Response.FromValue((T)(object)Copy(row, row.ETag), new FakeResponse());
            }
        }

        public override Response UpdateEntity<T>(
            T entity, ETag ifMatch, TableUpdateMode mode = TableUpdateMode.Merge,
            CancellationToken cancellationToken = default)
        {
            var row = (TableEntity)(object)entity;
            lock (gate)
            {
                if (!rows.TryGetValue((row.PartitionKey, row.RowKey), out var current))
                    throw new RequestFailedException(404, "Not found");
                if (ifMatch != ETag.All && current.ETag != ifMatch)
                    throw new RequestFailedException(412, "Conflict");
                rows[(row.PartitionKey, row.RowKey)] = Copy(row, new ETag((++revision).ToString()));
                if (row.PartitionKey == "image-build-queue" && row.GetString("State") == "Claimed" && FailAfterNextClaim)
                {
                    FailAfterNextClaim = false;
                    AmbiguousClaim.TrySetResult();
                    throw new RequestFailedException(503, "Claim response lost");
                }
                return new FakeResponse();
            }
        }

        public override Pageable<T> Query<T>(
            string? filter = null, int? maxPerPage = null, IEnumerable<string>? select = null,
            CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                var partition = filter!.Contains("image-build-queue") ? "image-build-queue" : "runner-images";
                var matches = rows.Values.Where(row => row.PartitionKey == partition
                    && (partition != "image-build-queue" || row.GetString("State") == "Pending"))
                    .Select(row => (T)(object)Copy(row, row.ETag)).ToArray();
                return Pageable<T>.FromPages([Page<T>.FromValues(matches, null, new FakeResponse())]);
            }
        }

        private static TableEntity Copy(TableEntity row, ETag etag)
        {
            var copy = new TableEntity(row.PartitionKey, row.RowKey);
            foreach (var property in row)
                copy[property.Key] = property.Value;
            copy.ETag = etag;
            return copy;
        }
    }

    private sealed class FakeResponse : Response
    {
        public override int Status => 200;
        public override string ReasonPhrase => "OK";
        public override Stream? ContentStream { get; set; }
        public override string ClientRequestId { get; set; } = "";
        public override void Dispose() { }
        protected override bool ContainsHeader(string name) => false;
        protected override IEnumerable<HttpHeader> EnumerateHeaders() => [];
        protected override bool TryGetHeader(string name, out string value) { value = ""; return false; }
        protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values) { values = []; return false; }
    }
}
