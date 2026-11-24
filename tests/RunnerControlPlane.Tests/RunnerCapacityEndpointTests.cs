using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using Azure;
using Azure.Core;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using RunnerControlPlane.Configuration;
using RunnerControlPlane.RunnerCapacity;

namespace RunnerControlPlane.Tests;

public sealed class RunnerCapacityEndpointTests
{
    [Fact]
    public async Task TableStateIsSharedAndConcurrentOperationsFailWithoutChangingCapacity()
    {
        var table = new FakeTableClient();
        var scaleSet = new FakeAzureVmScaleSetClient(1);
        using var first = new RunnerCapacityControlPlaneFactory(0, 0, 1, table: table, sharedScaleSet: scaleSet);
        using var second = new RunnerCapacityControlPlaneFactory(0, 0, 1, table: table, sharedScaleSet: scaleSet);
        using var a = first.CreateClient();
        using var b = second.CreateClient();
        a.DefaultRequestHeaders.Add("X-Test-Role", "operator");
        b.DefaultRequestHeaders.Add("X-Test-Role", "operator");

        Assert.Equal(HttpStatusCode.OK, (await a.PutAsJsonAsync("/api/runner-capacity/settings",
            new UpdateCapacitySettingsRequest(0, 2))).StatusCode);
        Assert.Equal(2, (await b.GetFromJsonAsync<CapacitySnapshot>("/api/runner-capacity"))!.MaximumCapacity);

        scaleSet.BlockNextSet();
        var pending = a.PostAsJsonAsync("/api/runner-capacity/scale-out", new ScaleOutRequest(3));
        await scaleSet.SetStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var conflict = await b.PutAsJsonAsync("/api/runner-capacity/settings", new UpdateCapacitySettingsRequest(0, 4));
            Assert.NotEqual(HttpStatusCode.OK, conflict.StatusCode);
            Assert.Equal(0, scaleSet.Capacity);
        }
        finally
        {
            scaleSet.ReleaseSet();
        }
        Assert.Equal(HttpStatusCode.OK, (await pending).StatusCode);
        Assert.Equal(2, scaleSet.Capacity);
        Assert.Equal(2, (await b.GetFromJsonAsync<CapacitySnapshot>("/api/runner-capacity"))!.MaximumCapacity);
        var audit = await b.GetFromJsonAsync<CapacityAuditRecord[]>("/api/runner-capacity/audit");
        Assert.Equal(2, audit!.Length);
        Assert.Contains(audit, record => record.Action == "request-scale-out" && record.Outcome.Contains("capped"));
    }

    [Fact]
    public async Task ExplicitJsonImportPreservesLargeAuditAndRejectsExistingState()
    {
        var table = new FakeTableClient();
        var store = new AzureTableRunnerCapacityStateStore(table);
        var path = Path.Combine(Directory.GetCurrentDirectory(), $"capacity-import-{Guid.NewGuid():N}.json");
        var audit = Enumerable.Range(0, 205)
            .Select(i => new RunnerControlPlane.RunnerCapacity.CapacityAuditRecord(
                $"operator-{i}", DateTimeOffset.UtcNow.AddSeconds(i), "update-capacity-settings", "succeeded"))
            .ToArray();
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new RunnerCapacityState(1, 7, audit)));
            await store.ImportJsonAsync(path);
            using var factory = new RunnerCapacityControlPlaneFactory(0, 0, 1, table: table);
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Test-Role", "reader");
            Assert.Equal(7, (await client.GetFromJsonAsync<CapacitySnapshot>("/api/runner-capacity"))!.MaximumCapacity);
            Assert.Equal(205, (await client.GetFromJsonAsync<CapacityAuditRecord[]>("/api/runner-capacity/audit"))!.Length);
            await Assert.ThrowsAsync<RunnerCapacityIntegrationException>(() => store.ImportJsonAsync(path));
            Assert.Equal(205, store.Load()!.AuditTrail.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FailedCompletionLeavesOperationLockedAndDoesNotReportSuccess()
    {
        var table = new FakeTableClient();
        var scaleSet = new FakeAzureVmScaleSetClient(1);
        using var first = new RunnerCapacityControlPlaneFactory(0, 0, 1, table: table, sharedScaleSet: scaleSet);
        using var second = new RunnerCapacityControlPlaneFactory(0, 0, 1, table: table, sharedScaleSet: scaleSet);
        using var a = first.CreateClient();
        using var b = second.CreateClient();
        a.DefaultRequestHeaders.Add("X-Test-Role", "operator");
        b.DefaultRequestHeaders.Add("X-Test-Role", "operator");
        scaleSet.BlockNextSet();
        var pending = a.PostAsJsonAsync("/api/runner-capacity/scale-out", new ScaleOutRequest(1));
        await scaleSet.SetStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        table.FailNextTransaction = true;
        scaleSet.ReleaseSet();
        Assert.NotEqual(HttpStatusCode.OK, (await pending).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await b.PostAsJsonAsync("/api/runner-capacity/scale-out", new ScaleOutRequest(1))).StatusCode);
        Assert.Equal(2, scaleSet.Capacity);
        var audit = await b.GetFromJsonAsync<CapacityAuditRecord[]>("/api/runner-capacity/audit");
        Assert.Equal("in-progress", Assert.Single(audit!).Outcome);
    }

    [Fact]
    public async Task EtagConflictDoesNotApplyScaleOutOrCreateAudit()
    {
        var table = new FakeTableClient { ConflictNextTransaction = true };
        using var factory = new RunnerCapacityControlPlaneFactory(0, 0, 1, table: table);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");
        var response = await client.PostAsJsonAsync("/api/runner-capacity/scale-out", new ScaleOutRequest(1));
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, factory.AzureScaleSet.Capacity);
        Assert.Empty((await client.GetFromJsonAsync<CapacityAuditRecord[]>("/api/runner-capacity/audit"))!);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/runner-capacity/scale-out", new ScaleOutRequest(1))).StatusCode);
    }

    [Fact]
    public async Task UncertainAzureChangeLeavesLockAndFailedAudit()
    {
        var table = new FakeTableClient();
        var scaleSet = new FakeAzureVmScaleSetClient(1) { FailAfterSet = true };
        using var factory = new RunnerCapacityControlPlaneFactory(0, 0, 1, table: table, sharedScaleSet: scaleSet);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");
        Assert.NotEqual(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/runner-capacity/scale-out", new ScaleOutRequest(1))).StatusCode);
        Assert.Equal(2, scaleSet.Capacity);
        Assert.Equal("failed", Assert.Single((await client.GetFromJsonAsync<CapacityAuditRecord[]>("/api/runner-capacity/audit"))!).Outcome);
        Assert.NotEqual(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/runner-capacity/scale-out", new ScaleOutRequest(1))).StatusCode);
    }

    [Fact]
    public async Task FailedImportRemainsLockedAndCannotBeRetriedOverPartialData()
    {
        var table = new FakeTableClient { FailAddNumber = 3 };
        var store = new AzureTableRunnerCapacityStateStore(table);
        var path = Path.Combine(Directory.GetCurrentDirectory(), $"capacity-import-{Guid.NewGuid():N}.json");
        try
        {
            var record = new RunnerControlPlane.RunnerCapacity.CapacityAuditRecord("operator", DateTimeOffset.UtcNow, "action", "succeeded");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new RunnerCapacityState(1, 4, [record, record])));
            await Assert.ThrowsAsync<RunnerCapacityIntegrationException>(() => store.ImportJsonAsync(path));
            Assert.Throws<RunnerCapacityIntegrationException>(() => store.Load());
            await Assert.ThrowsAsync<RunnerCapacityIntegrationException>(() => store.ImportJsonAsync(path));
            await Assert.ThrowsAsync<RunnerCapacityIntegrationException>(() => store.AcquireAsync(1, 4, null, default));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReaderCanViewCapacityButCannotChangeItOrRequestScaleOut()
    {
        using var factory = new RunnerCapacityControlPlaneFactory(queuedJobs: 1, busyRunners: 1, capacity: 2);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "reader");

        var website = await client.GetStringAsync("/");
        var snapshot = await client.GetFromJsonAsync<CapacitySnapshot>("/api/runner-capacity");
        var update = await client.PutAsJsonAsync(
            "/api/runner-capacity/settings",
            new { minimumCapacity = 0, maximumCapacity = 4 });
        var scaleOut = await client.PostAsJsonAsync(
            "/api/runner-capacity/scale-out",
            new { count = 1 });

        Assert.Contains("Queued eligible jobs", website);
        Assert.Contains("capacity-settings-form", website);
        Assert.Equal(1, snapshot!.MinimumCapacity);
        Assert.Equal(4, snapshot.MaximumCapacity);
        Assert.Equal(2, snapshot.CurrentCapacity);
        Assert.Equal(1, snapshot.BusyRunners);
        Assert.Equal(1, snapshot.QueuedJobs);
        Assert.False(snapshot.IsCapped);
        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, scaleOut.StatusCode);
    }

    [Fact]
    public async Task QueuedDemandScalesUpToTheCapAndFallsBackToTheMinimum()
    {
        using var factory = new RunnerCapacityControlPlaneFactory(
            queuedJobs: 5,
            busyRunners: 0,
            capacity: 1,
            minimumCapacity: 1,
            maximumCapacity: 3,
            reconcileIntervalSeconds: 1);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "reader");

        await WaitForCapacityAsync(factory, 3);
        var capped = await client.GetFromJsonAsync<CapacitySnapshot>("/api/runner-capacity");

        Assert.Equal(3, capped!.CurrentCapacity);
        Assert.Equal(5, capped.QueuedJobs);
        Assert.Equal(2, capped.UnservedJobs);
        Assert.True(capped.IsCapped);

        factory.GitHubCapacity.QueuedJobs = 0;
        await WaitForCapacityAsync(factory, 1);
        var scaledIn = await client.GetFromJsonAsync<CapacitySnapshot>("/api/runner-capacity");

        Assert.Equal(1, scaledIn!.CurrentCapacity);
        Assert.False(scaledIn.IsCapped);
    }

    [Fact]
    public async Task OperatorCanChangeCapacityAndRequestScaleOutWithinTheMaximumWithAudit()
    {
        using var factory = new RunnerCapacityControlPlaneFactory(
            queuedJobs: 0,
            busyRunners: 0,
            capacity: 1,
            reconcileIntervalSeconds: 3600);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");

        var settings = await client.PutAsJsonAsync(
            "/api/runner-capacity/settings",
            new UpdateCapacitySettingsRequest(0, 2));
        var scaleOut = await client.PostAsJsonAsync(
            "/api/runner-capacity/scale-out",
            new ScaleOutRequest(5));
        var result = await scaleOut.Content.ReadFromJsonAsync<ScaleOutResult>();
        var audit = await client.GetFromJsonAsync<CapacityAuditRecord[]>("/api/runner-capacity/audit");

        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
        Assert.Equal(HttpStatusCode.OK, scaleOut.StatusCode);
        Assert.Equal(5, result!.RequestedCount);
        Assert.Equal(2, result.AddedCount);
        Assert.True(result.IsCapped);
        Assert.Equal(2, result.Capacity.CurrentCapacity);
        Assert.Equal(2, audit!.Length);
        Assert.All(audit, entry => Assert.Equal("Capacity Test Operator", entry.Actor));
        Assert.Equal("update-capacity-settings", audit![0].Action);
        Assert.Equal("succeeded", audit[0].Outcome);
        Assert.Equal("request-scale-out", audit![1].Action);
        Assert.Contains("capped", audit[1].Outcome);
        Assert.All(audit, entry => Assert.NotEqual(default, entry.Timestamp));
    }

    [Fact]
    public async Task OperatorCannotSetMinimumAboveMaximum()
    {
        using var factory = new RunnerCapacityControlPlaneFactory(queuedJobs: 0, busyRunners: 0, capacity: 1);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");

        var update = await client.PutAsJsonAsync(
            "/api/runner-capacity/settings",
            new UpdateCapacitySettingsRequest(3, 2));
        var snapshot = await client.GetFromJsonAsync<CapacitySnapshot>("/api/runner-capacity");
        var audit = await client.GetFromJsonAsync<CapacityAuditRecord[]>("/api/runner-capacity/audit");

        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
        Assert.Equal(1, snapshot!.MinimumCapacity);
        Assert.Equal(4, snapshot.MaximumCapacity);
        Assert.Empty(audit!);
    }

    [Fact]
    public async Task CapacitySettingsAndAuditSurviveAControlPlaneRestart()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"runner-capacity-{Guid.NewGuid():N}.json");
        try
        {
            using (var factory = new RunnerCapacityControlPlaneFactory(
                       queuedJobs: 0,
                       busyRunners: 0,
                       capacity: 1,
                       stateFilePath: statePath))
            using (var client = factory.CreateClient())
            {
                client.DefaultRequestHeaders.Add("X-Test-Role", "operator");
                var settings = await client.PutAsJsonAsync(
                    "/api/runner-capacity/settings",
                    new UpdateCapacitySettingsRequest(0, 3));
                Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
            }

            using var restartedFactory = new RunnerCapacityControlPlaneFactory(
                queuedJobs: 0,
                busyRunners: 0,
                capacity: 0,
                stateFilePath: statePath);
            using var restartedClient = restartedFactory.CreateClient();
            restartedClient.DefaultRequestHeaders.Add("X-Test-Role", "reader");

            var snapshot = await restartedClient.GetFromJsonAsync<CapacitySnapshot>("/api/runner-capacity");
            var audit = await restartedClient.GetFromJsonAsync<CapacityAuditRecord[]>("/api/runner-capacity/audit");

            Assert.Equal(0, snapshot!.MinimumCapacity);
            Assert.Equal(3, snapshot!.MaximumCapacity);
            Assert.Single(audit!);
            Assert.Equal("update-capacity-settings", audit![0].Action);
        }
        finally
        {
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }
        }
    }

    private static async Task WaitForCapacityAsync(RunnerCapacityControlPlaneFactory factory, int expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!timeout.IsCancellationRequested)
        {
            if (factory.AzureScaleSet.Capacity == expected)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
        }

        throw new TimeoutException($"Runner VM Scale Set did not reach capacity {expected}.");
    }

    private sealed record CapacitySnapshot(
        int MinimumCapacity,
        int MaximumCapacity,
        int CurrentCapacity,
        int BusyRunners,
        int QueuedJobs,
        int UnservedJobs,
        bool IsCapped);

    private sealed record ScaleOutResult(
        int RequestedCount,
        int AddedCount,
        bool IsCapped,
        CapacitySnapshot Capacity);

    private sealed record CapacityAuditRecord(
        string Actor,
        DateTimeOffset Timestamp,
        string Action,
        string Outcome);

    [Fact]
    public async Task DisabledWebWorkersDoNotReconcileAcrossRestartsButWorkerHostResumes()
    {
        var scaleSet = new FakeAzureVmScaleSetClient(0);
        scaleSet.BlockNextSet();
        using var worker = new RunnerCapacityControlPlaneFactory(3, 0, 0, sharedScaleSet: scaleSet);
        using var workerClient = worker.CreateClient();
        await scaleSet.SetStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            for (var deployment = 0; deployment < 2; deployment++)
            {
                using var staging = new RunnerCapacityControlPlaneFactory(3, 0, 0,
                    sharedScaleSet: scaleSet, backgroundWorkersEnabled: false);
                using var client = staging.CreateClient();
                client.DefaultRequestHeaders.Add("X-Test-Role", "reader");
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/runner-capacity")).StatusCode);
                await Task.Delay(150);
                Assert.Equal(0, scaleSet.Capacity);
            }
        }
        finally
        {
            scaleSet.ReleaseSet();
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (scaleSet.Capacity != 3 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.Equal(3, scaleSet.Capacity);
    }

    private sealed class RunnerCapacityControlPlaneFactory(
        int queuedJobs,
        int busyRunners,
        int capacity,
        int minimumCapacity = 1,
        int maximumCapacity = 4,
        int reconcileIntervalSeconds = 3600,
        string? stateFilePath = null,
        TableClient? table = null,
        FakeAzureVmScaleSetClient? sharedScaleSet = null,
        bool backgroundWorkersEnabled = true) : WebApplicationFactory<Program>
    {
        public FakeGitHubRunnerCapacityClient GitHubCapacity { get; } =
            new(queuedJobs, busyRunners);

        public FakeAzureVmScaleSetClient AzureScaleSet { get; } = sharedScaleSet ?? new(capacity);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.Sources.Clear();
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Entra:TenantId"] = "tenant-id",
                    ["Entra:ClientId"] = "client-id",
                    ["Entra:ClientSecret"] = "client-secret",
                    ["Entra:ReadOnlyGroupId"] = "reader-group",
                    ["Entra:OperatorGroupId"] = "operator-group",
                    ["GitHub:Organization"] = "octo",
                    ["GitHub:Token"] = "test-token",
                    ["GitHub:RunnerGroupId"] = "42",
                    ["Runner:Label"] = "linux-dotnet",
                    ["RunnerCapacity:MinimumCapacity"] = minimumCapacity.ToString(),
                    ["RunnerCapacity:MaximumCapacity"] = maximumCapacity.ToString(),
                    ["RunnerCapacity:ReconcileIntervalSeconds"] = reconcileIntervalSeconds.ToString(),
                    ["RunnerCapacity:StateFilePath"] = stateFilePath ?? "",
                    ["BackgroundWorkers:Enabled"] = backgroundWorkersEnabled.ToString()
                });
            });

            builder.ConfigureTestServices(services =>
            {
                if (table is not null)
                {
                    services.RemoveAll<IRunnerCapacityStateStore>();
                    services.AddSingleton<IRunnerCapacityStateStore>(new AzureTableRunnerCapacityStateStore(table));
                    var worker = services.Single(d => d.ServiceType == typeof(IHostedService)
                        && d.ImplementationType == typeof(ConditionalHostedService<RunnerCapacityReconcileWorker>));
                    services.Remove(worker);
                }
                services.RemoveAll<IGitHubRunnerCapacityClient>();
                services.AddSingleton<IGitHubRunnerCapacityClient>(GitHubCapacity);
                services.RemoveAll<IAzureVmScaleSetClient>();
                services.AddSingleton<IAzureVmScaleSetClient>(AzureScaleSet);
                services.AddAuthentication()
                    .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("CapacityTest", _ => { });
                services.PostConfigure<AuthenticationOptions>(options =>
                {
                    options.DefaultScheme = "CapacityTest";
                    options.DefaultAuthenticateScheme = "CapacityTest";
                    options.DefaultChallengeScheme = "CapacityTest";
                    options.DefaultForbidScheme = "CapacityTest";
                });
            });
        }
    }

    private sealed class FakeGitHubRunnerCapacityClient(int queuedJobs, int busyRunners)
        : IGitHubRunnerCapacityClient
    {
        public int QueuedJobs
        {
            get => Volatile.Read(ref queuedJobs);
            set => Interlocked.Exchange(ref queuedJobs, value);
        }

        public Task<RunnerDemand> GetDemandAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new RunnerDemand(QueuedJobs, busyRunners));
        }
    }

    private sealed class FakeAzureVmScaleSetClient(int capacity) : IAzureVmScaleSetClient
    {
        private TaskCompletionSource? _release;
        public bool FailAfterSet { get; set; }
        public TaskCompletionSource SetStarted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Capacity => Volatile.Read(ref capacity);

        public void BlockNextSet()
        {
            SetStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ReleaseSet() => _release!.TrySetResult();

        public Task<int> GetCapacityAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Capacity);

        public Task<int> SetCapacityAsync(
            int desiredCapacity,
            IReadOnlyList<string> busyRunnerNames,
            CancellationToken cancellationToken) =>
            SetCapacityAsyncCore(desiredCapacity);

        private async Task<int> SetCapacityAsyncCore(int desiredCapacity)
        {
            if (_release is { } release)
            {
                SetStarted.TrySetResult();
                await release.Task;
                _release = null;
            }
            Interlocked.Exchange(ref capacity, desiredCapacity);
            if (FailAfterSet)
                throw new RunnerCapacityIntegrationException("Injected uncertain VM operation", new InvalidOperationException());
            return desiredCapacity;
        }
    }

    private sealed class FakeTableClient : TableClient
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, TableEntity> _rows = [];
        private int _version;
        private int _addCount;
        public bool FailNextTransaction { get; set; }
        public bool ConflictNextTransaction { get; set; }
        public int FailAddNumber { get; set; }
        private static readonly Response Response = new FakeResponse();

        private static TableEntity Copy(TableEntity value)
        {
            var copy = new TableEntity(value.PartitionKey, value.RowKey) { ETag = value.ETag };
            foreach (var item in value)
                copy[item.Key] = item.Value;
            return copy;
        }

        public override Response<T> GetEntity<T>(string partitionKey, string rowKey,
            IEnumerable<string>? select = null, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                if (!_rows.TryGetValue(rowKey, out var value))
                    throw new RequestFailedException(404, "Not found", "ResourceNotFound", null);
                return Azure.Response.FromValue((T)(object)Copy(value), Response);
            }
        }

        public override Pageable<T> Query<T>(string? filter = null, int? maxPerPage = null,
            IEnumerable<string>? select = null, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                var values = _rows.Values.Where(x => filter is null || !filter.Contains("RowKey ge") || x.RowKey.StartsWith("audit-"))
                    .OrderBy(x => x.RowKey).Select(x => (T)(object)Copy(x)).ToArray();
                return Pageable<T>.FromPages([Page<T>.FromValues(values, null, Response)]);
            }
        }

        public override Task<Response> AddEntityAsync<T>(T entity, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                if (++_addCount == FailAddNumber)
                    throw new RequestFailedException(503, "Injected add failure");
                if (_rows.ContainsKey(entity.RowKey))
                    throw new RequestFailedException(409, "Already exists");
                Write((TableEntity)(object)entity);
                return Task.FromResult(Response);
            }
        }

        public override Task<Response> UpdateEntityAsync<T>(T entity, ETag ifMatch,
            TableUpdateMode mode = TableUpdateMode.Merge, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                Check(entity.RowKey, ifMatch);
                Write((TableEntity)(object)entity);
                return Task.FromResult(Response);
            }
        }

        public override Task<Response<IReadOnlyList<Response>>> SubmitTransactionAsync(
            IEnumerable<TableTransactionAction> actions, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                if (FailNextTransaction)
                {
                    FailNextTransaction = false;
                    throw new RequestFailedException(503, "Injected failure");
                }
                var batch = actions.ToArray();
                if (ConflictNextTransaction)
                {
                    ConflictNextTransaction = false;
                    Write(_rows["settings"]);
                }
                foreach (var action in batch)
                {
                    if (action.ActionType == TableTransactionActionType.Add)
                    {
                        if (_rows.ContainsKey(action.Entity.RowKey))
                            throw new RequestFailedException(409, "Already exists");
                    }
                    else
                        Check(action.Entity.RowKey, action.ETag);
                }
                foreach (var action in batch)
                    Write((TableEntity)action.Entity);
                return Task.FromResult(Azure.Response.FromValue<IReadOnlyList<Response>>([], Response));
            }
        }

        private void Check(string key, ETag match)
        {
            if (!_rows.TryGetValue(key, out var existing) || (match != ETag.All && existing.ETag != match))
                throw new RequestFailedException(412, "ETag mismatch");
        }

        private void Write(TableEntity entity)
        {
            var copy = Copy(entity);
            copy.ETag = new ETag((++_version).ToString());
            _rows[copy.RowKey] = copy;
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
        protected override bool TryGetHeader(string name, [NotNullWhen(true)] out string? value) { value = null; return false; }
        protected override bool TryGetHeaderValues(string name, [NotNullWhen(true)] out IEnumerable<string>? values) { values = null; return false; }
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var group = Request.Headers["X-Test-Role"].ToString() switch
            {
                "reader" => "reader-group",
                "operator" => "operator-group",
                _ => null
            };
            if (group is null)
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [new Claim("groups", group), new Claim("name", "Capacity Test Operator")],
                "CapacityTest");
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), "CapacityTest")));
        }
    }
}
