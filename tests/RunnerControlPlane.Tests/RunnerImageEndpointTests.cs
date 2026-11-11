using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RunnerControlPlane.Images;
using RunnerControlPlane.RunnerCapacity;
using RunnerControlPlane.RunnerVms;

namespace RunnerControlPlane.Tests;

public sealed class RunnerImageEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task ReaderCanInspectImagesButCannotRequestAnExpeditedBuild()
    {
        using var factory = new RunnerImageControlPlaneFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "reader");

        var images = await client.GetAsync("/api/runner-images");
        var build = await client.PostAsJsonAsync(
            "/api/runner-images",
            new { ubuntuRelease = "24.04", dotNetSdks = new[] { "10.0.100" }, packages = new { curl = "8.5.0" }, runnerVersion = "2.331.0", expedited = true });

        Assert.Equal(HttpStatusCode.OK, images.StatusCode);
        Assert.Equal("[]", await images.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, build.StatusCode);
    }

    [Fact]
    public async Task OperatorCanBuildAndInspectAValidatedImageInventory()
    {
        using var factory = new RunnerImageControlPlaneFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");

        var response = await RequestBuildAsync(client);
        var queued = await response.Content.ReadFromJsonAsync<RunnerImage>(JsonOptions);
        var completed = await WaitForStatusAsync(client, queued!.Version, RunnerImageBuildStatus.Complete);
        var available = await client.GetFromJsonAsync<RunnerImage[]>("/api/runner-images/available", JsonOptions);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(queued.Expedited);
        Assert.Equal("24.04", queued.BuildRequest.UbuntuRelease);
        Assert.Equal(RunnerImageBuildStatus.Complete, completed.Status);
        Assert.Equal("24.04", completed.Inventory!.UbuntuRelease);
        Assert.Equal(["8.0.404", "9.0.203"], completed.Inventory.DotNetSdks);
        Assert.Equal("8.5.0", completed.Inventory.Packages["curl"]);
        Assert.Equal("2.331.0", completed.Inventory.RunnerVersion);
        Assert.Contains(available!, image => image.Version == queued.Version);
    }

    [Fact]
    public async Task FailedValidationNeverMakesAnImageAvailable()
    {
        using var factory = new RunnerImageControlPlaneFactory(failValidation: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");

        var response = await RequestBuildAsync(client);
        var queued = await response.Content.ReadFromJsonAsync<RunnerImage>(JsonOptions);
        var failed = await WaitForStatusAsync(client, queued!.Version, RunnerImageBuildStatus.Failed);
        var available = await client.GetFromJsonAsync<RunnerImage[]>("/api/runner-images/available", JsonOptions);

        Assert.Equal("The sample .NET build failed.", failed.FailureReason);
        Assert.DoesNotContain(available!, image => image.Version == queued.Version);
    }

    [Fact]
    public async Task ExpeditedBuildRunsBeforeBuildsAlreadyWaitingInTheQueue()
    {
        using var factory = new RunnerImageControlPlaneFactory(holdBuilds: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");

        var firstResponse = await RequestBuildAsync(client, expedited: false);
        var first = await firstResponse.Content.ReadFromJsonAsync<RunnerImage>(JsonOptions);
        await factory.Builder.FirstBuildStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var normalResponse = await RequestBuildAsync(client, expedited: false);
        var normal = await normalResponse.Content.ReadFromJsonAsync<RunnerImage>(JsonOptions);
        var expeditedResponse = await RequestBuildAsync(client, expedited: true);
        var expedited = await expeditedResponse.Content.ReadFromJsonAsync<RunnerImage>(JsonOptions);

        factory.Builder.ReleaseFirstBuild();
        await factory.Builder.SecondBuildStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var expeditedStatus = await WaitForStatusAsync(
            client,
            expedited!.Version,
            RunnerImageBuildStatus.Building);
        var normalStatus = await client.GetFromJsonAsync<RunnerImage>(
            $"/api/runner-images/{Uri.EscapeDataString(normal!.Version)}",
            JsonOptions);

        Assert.NotEqual(first!.Version, expeditedStatus.Version);
        Assert.True(expeditedStatus.Expedited);
        Assert.Equal(RunnerImageBuildStatus.Queued, normalStatus!.Status);
        factory.Builder.ReleaseSecondBuild();
    }

    [Fact]
    public async Task UnpinnedPackageVersionsAreRejected()
    {
        using var factory = new RunnerImageControlPlaneFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");

        var response = await client.PostAsJsonAsync(
            "/api/runner-images",
            new RunnerImageBuildRequest(
                "24.04",
                ["8.0.404"],
                new Dictionary<string, string> { ["curl"] = "latest" },
                "2.331.0",
                Expedited: true));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await client.GetFromJsonAsync<RunnerImage[]>("/api/runner-images", JsonOptions) ?? []);
    }

    [Fact]
    public async Task NonLtsUbuntuReleaseIsRejected()
    {
        using var factory = new RunnerImageControlPlaneFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");

        var response = await client.PostAsJsonAsync(
            "/api/runner-images",
            new RunnerImageBuildRequest(
                "24.10",
                ["8.0.404"],
                new Dictionary<string, string> { ["curl"] = "8.5.0" },
                "2.331.0"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await client.GetFromJsonAsync<RunnerImage[]>("/api/runner-images", JsonOptions) ?? []);
    }

    [Fact]
    public async Task ImageWithInventoryThatDiffersFromThePinnedRequestIsRejected()
    {
        using var factory = new RunnerImageControlPlaneFactory(mismatchInventory: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");

        var response = await RequestBuildAsync(client);
        var queued = await response.Content.ReadFromJsonAsync<RunnerImage>(JsonOptions);
        var failed = await WaitForStatusAsync(client, queued!.Version, RunnerImageBuildStatus.Failed);
        var available = await client.GetFromJsonAsync<RunnerImage[]>("/api/runner-images/available", JsonOptions);

        Assert.Contains("did not match the pinned Ubuntu or GitHub Actions Runner version", failed.FailureReason);
        Assert.Empty(available!);
    }

    [Fact]
    public async Task ImageWithUnrequestedPackagesIsNotMadeAvailable()
    {
        using var factory = new RunnerImageControlPlaneFactory(extraPackage: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");

        var response = await RequestBuildAsync(client);
        var queued = await response.Content.ReadFromJsonAsync<RunnerImage>(JsonOptions);
        var failed = await WaitForStatusAsync(client, queued!.Version, RunnerImageBuildStatus.Failed);
        var available = await client.GetFromJsonAsync<RunnerImage[]>("/api/runner-images/available", JsonOptions);

        Assert.Contains("did not contain exactly the pinned package set", failed.FailureReason);
        Assert.Empty(available!);
    }

    [Fact]
    public async Task OperatorCanStartACanaryRolloutForAValidatedImage()
    {
        using var factory = new RunnerImageControlPlaneFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");

        var response = await RequestBuildAsync(client);
        var image = await response.Content.ReadFromJsonAsync<RunnerImage>(JsonOptions);
        await WaitForStatusAsync(client, image!.Version, RunnerImageBuildStatus.Complete);

        var oldVm = await client.PostAsJsonAsync(
            "/api/runner-vms",
            new CreateRunnerVmRequest(image.Version, "linux-dotnet"));
        Assert.Equal(HttpStatusCode.Accepted, oldVm.StatusCode);

        var rolloutResponse = await client.PostAsJsonAsync(
            "/api/runner-images/rollout",
            new { imageVersion = image.Version });
        var rollout = await rolloutResponse.Content.ReadFromJsonAsync<RunnerImageRollout>(JsonOptions);

        Assert.Equal(HttpStatusCode.Accepted, rolloutResponse.StatusCode);
        Assert.Equal(image.Version, rollout!.ImageVersion);
        Assert.True(rollout.Stage == RunnerImageRolloutStage.Canary || rollout.Stage == RunnerImageRolloutStage.Pending);
        Assert.NotNull(rollout.Id);
    }

    [Fact]
    public async Task FailedCanaryRolloutRollsBackToThePreviousImageVersion()
    {
        using var factory = new RunnerImageControlPlaneFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");

        var firstResponse = await RequestBuildAsync(client);
        var first = await firstResponse.Content.ReadFromJsonAsync<RunnerImage>(JsonOptions);
        await WaitForStatusAsync(client, first!.Version, RunnerImageBuildStatus.Complete);

        var secondResponse = await RequestBuildAsync(client);
        var second = await secondResponse.Content.ReadFromJsonAsync<RunnerImage>(JsonOptions);
        await WaitForStatusAsync(client, second!.Version, RunnerImageBuildStatus.Complete);

        var createVm = await client.PostAsJsonAsync(
            "/api/runner-vms",
            new CreateRunnerVmRequest(first.Version, "linux-dotnet"));
        var vm = await createVm.Content.ReadFromJsonAsync<RunnerVmRecord>(JsonOptions);

        var start = await client.PostAsJsonAsync(
            "/api/runner-images/rollout",
            new { imageVersion = second.Version, previousImageVersion = first.Version });
        var rollout = await start.Content.ReadFromJsonAsync<RunnerImageRollout>(JsonOptions);

        var failed = await client.PostAsJsonAsync(
            $"/api/runner-images/rollouts/{rollout!.Id}/canary/failure",
            new { failureReason = "Canary health check failed." });
        var failedRollout = await failed.Content.ReadFromJsonAsync<RunnerImageRollout>(JsonOptions);

        Assert.Equal(HttpStatusCode.Accepted, failed.StatusCode);
        Assert.Equal(RunnerImageRolloutStage.Failed, failedRollout!.Stage);
        Assert.Equal("Canary health check failed.", failedRollout.FailureReason);

        var vms = await client.GetFromJsonAsync<RunnerVmRecord[]> ("/api/runner-vms", JsonOptions);
        Assert.All(vms!, vmRecord => Assert.Equal(first.Version, vmRecord.ImageVersion));
    }

    private static Task<HttpResponseMessage> RequestBuildAsync(HttpClient client, bool expedited = true)
    {
        return client.PostAsJsonAsync(
            "/api/runner-images",
            new RunnerImageBuildRequest(
                "24.04",
                ["8.0.404", "9.0.203"],
                new Dictionary<string, string> { ["curl"] = "8.5.0" },
                "2.331.0",
                Expedited: expedited));
    }

    private static async Task<RunnerImage> WaitForStatusAsync(
        HttpClient client,
        string version,
        RunnerImageBuildStatus expectedStatus)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!timeout.IsCancellationRequested)
        {
            var image = await client.GetFromJsonAsync<RunnerImage>(
                $"/api/runner-images/{Uri.EscapeDataString(version)}",
                JsonOptions,
                timeout.Token);
            if (image?.Status == expectedStatus)
            {
                return image;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }

        throw new TimeoutException($"Runner image did not reach status {expectedStatus}.");
    }

    private sealed class RunnerImageControlPlaneFactory : WebApplicationFactory<Program>
    {
        private readonly bool _failValidation;

        public RunnerImageControlPlaneFactory(
            bool failValidation = false,
            bool holdBuilds = false,
            bool mismatchInventory = false,
            bool extraPackage = false)
        {
            _failValidation = failValidation;
            Builder = new FakeRunnerImageBuilder(holdBuilds, mismatchInventory, extraPackage);
        }

        public FakeRunnerImageBuilder Builder { get; }

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
                    ["Runner:Label"] = "linux-dotnet"
                });
            });

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IGitHubRunnerCapacityClient>();
                services.AddSingleton<IGitHubRunnerCapacityClient, TestGitHubRunnerCapacityClient>();
                services.RemoveAll<IRunnerImageBuilder>();
                services.AddSingleton<IRunnerImageBuilder>(Builder);
                services.RemoveAll<IRunnerImageValidator>();
                services.AddSingleton<IRunnerImageValidator>(
                    new FakeRunnerImageValidator(_failValidation));
                services.AddAuthentication()
                    .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                        "Test",
                        _ => { });
                services.PostConfigure<AuthenticationOptions>(options =>
                {
                    options.DefaultScheme = "Test";
                    options.DefaultAuthenticateScheme = "Test";
                    options.DefaultChallengeScheme = "Test";
                    options.DefaultForbidScheme = "Test";
                });
            });
        }
    }

    public sealed class FakeRunnerImageBuilder(
        bool holdBuilds,
        bool mismatchInventory,
        bool extraPackage) : IRunnerImageBuilder
    {
        private int _buildCount;
        private readonly TaskCompletionSource _releaseFirstBuild = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseSecondBuild = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstBuildStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondBuildStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseFirstBuild() => _releaseFirstBuild.TrySetResult();

        public void ReleaseSecondBuild() => _releaseSecondBuild.TrySetResult();

        public async Task<RunnerImageArtifact> BuildAsync(
            string version,
            RunnerImageBuildRequest request,
            CancellationToken cancellationToken)
        {
            var buildNumber = Interlocked.Increment(ref _buildCount);
            if (holdBuilds && buildNumber == 1)
            {
                FirstBuildStarted.TrySetResult();
                await _releaseFirstBuild.Task.WaitAsync(cancellationToken);
            }
            else if (holdBuilds && buildNumber == 2)
            {
                SecondBuildStarted.TrySetResult();
                await _releaseSecondBuild.Task.WaitAsync(cancellationToken);
            }

            var packages = new Dictionary<string, string> { ["curl"] = "8.5.0" };
            if (extraPackage)
            {
                packages["unrequested-tool"] = "1.0.0";
            }

            return new RunnerImageArtifact(new RunnerImageInventory(
                "24.04",
                ["8.0.404", "9.0.203"],
                packages,
                mismatchInventory ? "2.330.0" : "2.331.0"));
        }
    }

    private sealed class FakeRunnerImageValidator(bool failValidation) : IRunnerImageValidator
    {
        public Task<RunnerImageValidationResult> ValidateAsync(
            RunnerImageArtifact image,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(failValidation
                ? new RunnerImageValidationResult(
                    true,
                    true,
                    true,
                    false,
                    "The sample .NET build failed.")
                : new RunnerImageValidationResult(true, true, true, true));
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

            var principal = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim("groups", group)], "Test"));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, "Test")));
        }
    }
}
