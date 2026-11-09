using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RunnerControlPlane.RunnerCapacity;

namespace RunnerControlPlane.Tests;

public sealed class RunnerCapacityEndpointTests
{
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

    private sealed class RunnerCapacityControlPlaneFactory(
        int queuedJobs,
        int busyRunners,
        int capacity,
        int minimumCapacity = 1,
        int maximumCapacity = 4,
        int reconcileIntervalSeconds = 3600,
        string? stateFilePath = null) : WebApplicationFactory<Program>
    {
        public FakeGitHubRunnerCapacityClient GitHubCapacity { get; } =
            new(queuedJobs, busyRunners);

        public FakeAzureVmScaleSetClient AzureScaleSet { get; } = new(capacity);

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
                    ["RunnerCapacity:StateFilePath"] = stateFilePath ?? ""
                });
            });

            builder.ConfigureTestServices(services =>
            {
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
        public int Capacity => Volatile.Read(ref capacity);

        public Task<int> GetCapacityAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Capacity);

        public Task<int> SetCapacityAsync(
            int desiredCapacity,
            IReadOnlyList<string> busyRunnerNames,
            CancellationToken cancellationToken) =>
            SetCapacityAsyncCore(desiredCapacity);

        private Task<int> SetCapacityAsyncCore(int desiredCapacity)
        {
            Interlocked.Exchange(ref capacity, desiredCapacity);
            return Task.FromResult(desiredCapacity);
        }
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
