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

        var completed = await RegisterAndCompleteBuildAsync(client);
        var available = await client.GetFromJsonAsync<RunnerImage[]>("/api/runner-images/available", JsonOptions);

        Assert.True(completed.Expedited);
        Assert.Equal("24.04", completed.BuildRequest.UbuntuRelease);
        Assert.Equal(RunnerImageBuildStatus.Complete, completed.Status);
        Assert.Equal("24.04", completed.Inventory!.UbuntuRelease);
        Assert.Equal(["8.0.404", "9.0.203"], completed.Inventory.DotNetSdks);
        Assert.Equal("8.5.0", completed.Inventory.Packages["curl"]);
        Assert.Equal("2.331.0", completed.Inventory.RunnerVersion);
        Assert.Contains(available!, image => image.Version == completed.Version);
    }

    [Fact]
    public async Task OperatorCanRegisterAnImageBuildAndReceiveItsManifestDigest()
    {
        using var factory = new RunnerImageControlPlaneFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");

        var response = await RequestBuildAsync(client);
        var registration = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(registration.TryGetProperty("buildId", out var buildId));
        Assert.False(string.IsNullOrWhiteSpace(buildId.GetString()));
        Assert.True(registration.TryGetProperty("manifestDigest", out var manifestDigest));
        Assert.Matches("^[a-f0-9]{64}$", manifestDigest.GetString());
    }

    [Fact]
    public async Task ConfiguredWorkloadIdentityCanRegisterAScheduledImageBuild()
    {
        using var factory = new RunnerImageControlPlaneFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "image-builder");

        var response = await RequestBuildAsync(client, expedited: false);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var registration = await response.Content.ReadFromJsonAsync<RunnerImage>(JsonOptions);
        Assert.NotNull(registration);
        Assert.False(registration.Expedited);
        Assert.False(string.IsNullOrWhiteSpace(registration.BuildId));
        Assert.Matches("^[a-f0-9]{64}$", registration.ManifestDigest);
    }

    [Theory]
    [InlineData("operator", HttpStatusCode.Accepted)]
    [InlineData("reader", HttpStatusCode.Forbidden)]
    [InlineData("wrong-image-builder", HttpStatusCode.Forbidden)]
    [InlineData("delegated-image-builder", HttpStatusCode.Forbidden)]
    [InlineData("scoped-image-builder", HttpStatusCode.Forbidden)]
    [InlineData("missing-idtyp-image-builder", HttpStatusCode.Forbidden)]
    [InlineData("appid-image-builder", HttpStatusCode.Accepted)]
    public async Task ScheduledImageBuildRegistrationRequiresOperatorOrConfiguredAppOnlyIdentity(
        string role,
        HttpStatusCode expectedStatus)
    {
        using var factory = new RunnerImageControlPlaneFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", role);

        var response = await RequestBuildAsync(client, expedited: false);

        Assert.Equal(expectedStatus, response.StatusCode);
    }

    [Fact]
    public async Task WorkloadIdentityCannotManageOtherResources()
    {
        using var factory = new RunnerImageControlPlaneFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "image-builder");

        var rollout = await client.PostAsJsonAsync("/api/runner-images/rollout", new { imageVersion = "image" });
        var capacity = await client.PostAsJsonAsync("/api/runner-capacity/scale-out", new { count = 1 });
        var vm = await client.PostAsJsonAsync("/api/runner-vms", new CreateRunnerVmRequest("image"));

        Assert.Equal(HttpStatusCode.Forbidden, rollout.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, capacity.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, vm.StatusCode);
    }

    [Fact]
    public async Task OperatorCanRegisterAnImageBuildWithItsRunnerChecksum()
    {
        using var factory = new RunnerImageControlPlaneFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");
        const string runnerSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        var response = await client.PostAsJsonAsync(
            "/api/runner-images",
            new
            {
                ubuntuRelease = "24.04",
                dotNetSdks = new[] { "10.0.101" },
                packages = new { curl = "8.5.0-2ubuntu10.7" },
                runnerVersion = "2.331.0",
                runnerSha256
            });
        var registration = await response.Content.ReadFromJsonAsync<RunnerImage>(JsonOptions);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(runnerSha256, registration!.BuildRequest.RunnerSha256);
        Assert.Equal(
            "434cf311fd065b45fc73eab677a957556d661af1588060a139033e97d693180a",
            registration.ManifestDigest);
    }

    [Fact]
    public async Task OperatorCannotRegisterAnImageBuildWithAnInvalidRunnerChecksum()
    {
        using var factory = new RunnerImageControlPlaneFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");

        var response = await client.PostAsJsonAsync(
            "/api/runner-images",
            new
            {
                ubuntuRelease = "24.04",
                dotNetSdks = new[] { "10.0.101" },
                packages = new { curl = "8.5.0-2ubuntu10.7" },
                runnerVersion = "2.331.0",
                runnerSha256 = "not-a-sha256"
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SuccessfulBuildResultMakesTheValidatedImageAvailable()
    {
        using var factory = new RunnerImageControlPlaneFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");

        var registrationResponse = await RequestBuildAsync(client);
        var registration = await registrationResponse.Content.ReadFromJsonAsync<RunnerImage>(JsonOptions);
        var resultResponse = await client.PostAsJsonAsync(
            $"/api/runner-images/{registration!.Version}/result",
            new
            {
                manifestDigest = registration.ManifestDigest,
                azureImageId = "/subscriptions/test/resourceGroups/images/providers/Microsoft.Compute/images/runner",
                inventory = new
                {
                    ubuntuRelease = "24.04",
                    dotNetSdks = new[] { "8.0.404", "9.0.203" },
                    packages = new { curl = "8.5.0" },
                    runnerVersion = "2.331.0"
                },
                validation = new
                {
                    imageChecksPassed = true,
                    vmBooted = true,
                    gitHubRunnerRegistered = true,
                    sampleDotNetBuildPassed = true
                },
                succeeded = true
            });
        var completed = await client.GetFromJsonAsync<RunnerImage>(
            $"/api/runner-images/{registration.Version}",
            JsonOptions);
        var available = await client.GetFromJsonAsync<RunnerImage[]>("/api/runner-images/available", JsonOptions);

        Assert.Equal(HttpStatusCode.OK, resultResponse.StatusCode);
        Assert.Equal(RunnerImageBuildStatus.Complete, completed!.Status);
        Assert.Equal(
            "/subscriptions/test/resourceGroups/images/providers/Microsoft.Compute/images/runner",
            completed.AzureImageId);
        Assert.True(completed.BuildResult!.Validation.IsSuccessful);
        Assert.Contains(available!, image => image.Version == registration.Version);
    }

    [Fact]
    public async Task ConfiguredWorkloadIdentityCanReportBuildResults()
    {
        using var factory = new RunnerImageControlPlaneFactory();
        using var operatorClient = factory.CreateClient();
        operatorClient.DefaultRequestHeaders.Add("X-Test-Role", "operator");
        var registrationResponse = await RequestBuildAsync(operatorClient);
        var registration = await registrationResponse.Content.ReadFromJsonAsync<RunnerImage>(JsonOptions);

        using var workloadClient = factory.CreateClient();
        workloadClient.DefaultRequestHeaders.Add("X-Test-Role", "image-builder");
        var result = await workloadClient.PostAsJsonAsync(
            $"/api/runner-images/{registration!.Version}/result",
            CreateResult(registration));

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
    }

    [Fact]
    public async Task DelegatedUserTokenForTheConfiguredClientCannotReportBuildResults()
    {
        using var factory = new RunnerImageControlPlaneFactory();
        using var operatorClient = factory.CreateClient();
        operatorClient.DefaultRequestHeaders.Add("X-Test-Role", "operator");
        var registrationResponse = await RequestBuildAsync(operatorClient);
        var registration = await registrationResponse.Content.ReadFromJsonAsync<RunnerImage>(JsonOptions);

        using var delegatedClient = factory.CreateClient();
        delegatedClient.DefaultRequestHeaders.Add("X-Test-Role", "delegated-image-builder");
        var result = await delegatedClient.PostAsJsonAsync(
            $"/api/runner-images/{registration!.Version}/result",
            CreateResult(registration));

        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
    }

    [Fact]
    public async Task ReaderCannotReportBuildResults()
    {
        using var factory = new RunnerImageControlPlaneFactory();
        using var operatorClient = factory.CreateClient();
        operatorClient.DefaultRequestHeaders.Add("X-Test-Role", "operator");
        var registrationResponse = await RequestBuildAsync(operatorClient);
        var registration = await registrationResponse.Content.ReadFromJsonAsync<RunnerImage>(JsonOptions);

        using var readerClient = factory.CreateClient();
        readerClient.DefaultRequestHeaders.Add("X-Test-Role", "reader");
        var result = await readerClient.PostAsJsonAsync(
            $"/api/runner-images/{registration!.Version}/result",
            CreateResult(registration));
        var unchanged = await operatorClient.GetFromJsonAsync<RunnerImage>(
            $"/api/runner-images/{registration.Version}",
            JsonOptions);

        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        Assert.Equal(RunnerImageBuildStatus.Building, unchanged!.Status);
    }

    [Fact]
    public async Task RepeatingTheSameResultIsIdempotentAndConflictingRetryIsRejected()
    {
        using var factory = new RunnerImageControlPlaneFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");
        var registrationResponse = await RequestBuildAsync(client);
        var registration = await registrationResponse.Content.ReadFromJsonAsync<RunnerImage>(JsonOptions);
        var result = CreateResult(registration!);

        var first = await client.PostAsJsonAsync(
            $"/api/runner-images/{registration!.Version}/result",
            result);
        var retry = await client.PostAsJsonAsync(
            $"/api/runner-images/{registration.Version}/result",
            result);
        var conflictingRetry = await client.PostAsJsonAsync(
            $"/api/runner-images/{registration.Version}/result",
            result with
            {
                Succeeded = false,
                FailureReason = "A conflicting failure."
            });
        var saved = await client.GetFromJsonAsync<RunnerImage>(
            $"/api/runner-images/{registration.Version}",
            JsonOptions);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflictingRetry.StatusCode);
        Assert.Equal(RunnerImageBuildStatus.Complete, saved!.Status);
        Assert.Equal(2, saved.AuditTrail!.Count);
    }

    [Fact]
    public async Task MismatchedManifestDigestIsRejectedWithoutChangingTheBuild()
    {
        using var factory = new RunnerImageControlPlaneFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");
        var registrationResponse = await RequestBuildAsync(client);
        var registration = await registrationResponse.Content.ReadFromJsonAsync<RunnerImage>(JsonOptions);

        var response = await client.PostAsJsonAsync(
            $"/api/runner-images/{registration!.Version}/result",
            CreateResult(registration) with { ManifestDigest = new string('0', 64) });
        var saved = await client.GetFromJsonAsync<RunnerImage>(
            $"/api/runner-images/{registration.Version}",
            JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(RunnerImageBuildStatus.Building, saved!.Status);
        Assert.Null(saved.BuildResult);
    }

    [Fact]
    public async Task BuildResultAndAuditSurviveAControlPlaneRestart()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"runner-image-{Guid.NewGuid():N}.json");
        try
        {
            string buildId;
            using (var factory = new RunnerImageControlPlaneFactory(statePath))
            using (var client = factory.CreateClient())
            {
                client.DefaultRequestHeaders.Add("X-Test-Role", "operator");
                var image = await RegisterAndCompleteBuildAsync(client);
                buildId = image.Version;
            }

            using var restartedFactory = new RunnerImageControlPlaneFactory(statePath);
            using var restartedClient = restartedFactory.CreateClient();
            restartedClient.DefaultRequestHeaders.Add("X-Test-Role", "reader");

            var imageAfterRestart = await restartedClient.GetFromJsonAsync<RunnerImage>(
                $"/api/runner-images/{buildId}",
                JsonOptions);
            var available = await restartedClient.GetFromJsonAsync<RunnerImage[]>(
                "/api/runner-images/available",
                JsonOptions);

            Assert.Equal(RunnerImageBuildStatus.Complete, imageAfterRestart!.Status);
            Assert.Equal("2.331.0", imageAfterRestart.Inventory!.RunnerVersion);
            Assert.Equal(2, imageAfterRestart.AuditTrail!.Count);
            Assert.Contains(available!, image => image.Version == buildId);
        }
        finally
        {
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }
        }
    }

    [Fact]
    public async Task FailedValidationNeverMakesAnImageAvailable()
    {
        using var factory = new RunnerImageControlPlaneFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");

        var existingImage = await RegisterAndCompleteBuildAsync(client);
        var response = await RequestBuildAsync(client);
        var registration = await response.Content.ReadFromJsonAsync<RunnerImage>(JsonOptions);
        var resultResponse = await client.PostAsJsonAsync(
            $"/api/runner-images/{registration!.Version}/result",
            CreateResult(registration, succeeded: false));
        var failed = await client.GetFromJsonAsync<RunnerImage>(
            $"/api/runner-images/{registration.Version}",
            JsonOptions);
        var available = await client.GetFromJsonAsync<RunnerImage[]>("/api/runner-images/available", JsonOptions);

        Assert.Equal(HttpStatusCode.OK, resultResponse.StatusCode);
        Assert.Equal("The sample .NET build failed.", failed!.FailureReason);
        Assert.Contains(available!, image => image.Version == existingImage.Version);
        Assert.DoesNotContain(available!, image => image.Version == registration.Version);
    }

    [Fact]
    public async Task SchedulingMetadataDoesNotChangeTheManifestDigest()
    {
        using var factory = new RunnerImageControlPlaneFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");

        var first = await RequestBuildAsync(client, expedited: false);
        var second = await RequestBuildAsync(client, expedited: true);
        var firstBuild = await first.Content.ReadFromJsonAsync<RunnerImage>(JsonOptions);
        var secondBuild = await second.Content.ReadFromJsonAsync<RunnerImage>(JsonOptions);
        var changedManifestResponse = await client.PostAsJsonAsync(
            "/api/runner-images",
            new RunnerImageBuildRequest(
                "24.04",
                ["8.0.404", "9.0.203"],
                new Dictionary<string, string> { ["curl"] = "8.5.1" },
                "2.331.0"));
        var changedManifest = await changedManifestResponse.Content.ReadFromJsonAsync<RunnerImage>(JsonOptions);
        var checksumResponse = await client.PostAsJsonAsync(
            "/api/runner-images",
            new RunnerImageBuildRequest(
                "24.04",
                ["8.0.404", "9.0.203"],
                new Dictionary<string, string> { ["curl"] = "8.5.0" },
                "2.331.0",
                RunnerSha256: new string('a', 64)));
        var checksumBuild = await checksumResponse.Content.ReadFromJsonAsync<RunnerImage>(JsonOptions);

        Assert.NotEqual(firstBuild!.BuildId, secondBuild!.BuildId);
        Assert.Equal(firstBuild.ManifestDigest, secondBuild.ManifestDigest);
        Assert.NotEqual(firstBuild.ManifestDigest, changedManifest!.ManifestDigest);
        Assert.NotEqual(firstBuild.ManifestDigest, checksumBuild!.ManifestDigest);
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
        using var factory = new RunnerImageControlPlaneFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");

        var response = await RequestBuildAsync(client);
        var queued = await response.Content.ReadFromJsonAsync<RunnerImage>(JsonOptions);
        var result = CreateResult(queued!) with
        {
            Inventory = CreateInventory() with { RunnerVersion = "2.330.0" }
        };
        var rejected = await client.PostAsJsonAsync(
            $"/api/runner-images/{queued!.Version}/result",
            result);
        var stillBuilding = await client.GetFromJsonAsync<RunnerImage>(
            $"/api/runner-images/{queued.Version}",
            JsonOptions);
        var available = await client.GetFromJsonAsync<RunnerImage[]>("/api/runner-images/available", JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(RunnerImageBuildStatus.Building, stillBuilding!.Status);
        Assert.Empty(available!);
    }

    [Fact]
    public async Task ImageWithUnrequestedPackagesIsNotMadeAvailable()
    {
        using var factory = new RunnerImageControlPlaneFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");

        var response = await RequestBuildAsync(client);
        var queued = await response.Content.ReadFromJsonAsync<RunnerImage>(JsonOptions);
        var result = CreateResult(queued!) with
        {
            Inventory = CreateInventory() with
            {
                Packages = new Dictionary<string, string>
                {
                    ["curl"] = "8.5.0",
                    ["unrequested-tool"] = "1.0.0"
                }
            }
        };
        var rejected = await client.PostAsJsonAsync(
            $"/api/runner-images/{queued!.Version}/result",
            result);
        var stillBuilding = await client.GetFromJsonAsync<RunnerImage>(
            $"/api/runner-images/{queued.Version}",
            JsonOptions);
        var available = await client.GetFromJsonAsync<RunnerImage[]>("/api/runner-images/available", JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(RunnerImageBuildStatus.Building, stillBuilding!.Status);
        Assert.Empty(available!);
    }

    [Fact]
    public async Task OperatorCanStartACanaryRolloutForAValidatedImage()
    {
        using var factory = new RunnerImageControlPlaneFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");

        var image = await RegisterAndCompleteBuildAsync(client);

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

        var first = await RegisterAndCompleteBuildAsync(client);
        var second = await RegisterAndCompleteBuildAsync(client);

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

    [Fact]
    public async Task RolloutAuditTrailReflectsTheFullCanaryLifecycle()
    {
        using var factory = new RunnerImageControlPlaneFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");

        var first = await RegisterAndCompleteBuildAsync(client);
        var second = await RegisterAndCompleteBuildAsync(client);

        var start = await client.PostAsJsonAsync(
            "/api/runner-images/rollouts",
            new { imageVersion = second.Version, previousImageVersion = first.Version });
        var rollout = await start.Content.ReadFromJsonAsync<RunnerImageRollout>(JsonOptions);

        var succeeded = await client.PostAsJsonAsync(
            $"/api/runner-images/rollouts/{rollout!.Id}/canary/success",
            new { });
        Assert.Equal(HttpStatusCode.OK, succeeded.StatusCode);

        var audit = await client.GetFromJsonAsync<RunnerImageRolloutAuditRecord[]>(
            "/api/runner-images/rollouts/audit",
            JsonOptions);

        Assert.NotEmpty(audit!);
        Assert.Contains(audit, record => record.Action == "request-rollout" && record.Outcome == "queued");
        Assert.Contains(audit, record => record.Action == "canary-started" && record.Outcome == "canary");
        Assert.Contains(audit, record => record.Action == "canary-succeeded" && record.Outcome == "succeeded");
        Assert.All(audit, record => Assert.NotEqual(default, record.Timestamp));
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

    private static async Task<RunnerImage> RegisterAndCompleteBuildAsync(HttpClient client)
    {
        var response = await RequestBuildAsync(client);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var image = await response.Content.ReadFromJsonAsync<RunnerImage>(JsonOptions);
        Assert.NotNull(image);

        var result = await client.PostAsJsonAsync(
            $"/api/runner-images/{image.Version}/result",
            CreateResult(image));
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);

        return await client.GetFromJsonAsync<RunnerImage>(
            $"/api/runner-images/{image.Version}",
            JsonOptions) ?? throw new InvalidOperationException("The registered image build was not found.");
    }

    private static RunnerImageBuildResult CreateResult(
        RunnerImage image,
        bool succeeded = true)
    {
        return new RunnerImageBuildResult(
            image.ManifestDigest!,
            "/subscriptions/test/resourceGroups/images/providers/Microsoft.Compute/images/runner",
            CreateInventory(),
            succeeded
                ? new RunnerImageValidationResult(true, true, true, true)
                : new RunnerImageValidationResult(
                    true,
                    true,
                    true,
                    false,
                    "The sample .NET build failed."),
            succeeded,
            succeeded ? null : "The sample .NET build failed.");
    }

    private static RunnerImageInventory CreateInventory()
    {
        return new RunnerImageInventory(
            "24.04",
            ["8.0.404", "9.0.203"],
            new Dictionary<string, string> { ["curl"] = "8.5.0" },
            "2.331.0");
    }

    private sealed class RunnerImageControlPlaneFactory : WebApplicationFactory<Program>
    {
        private readonly string? _stateFilePath;

        public RunnerImageControlPlaneFactory(string? stateFilePath = null)
        {
            _stateFilePath = stateFilePath;
        }

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
                    ["RunnerImageBuild:StateFilePath"] = _stateFilePath ?? "",
                    ["RunnerImageBuild:WorkloadClientId"] = "image-builder-client"
                });
            });

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IGitHubRunnerCapacityClient>();
                services.AddSingleton<IGitHubRunnerCapacityClient, TestGitHubRunnerCapacityClient>();
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

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = Request.Headers["X-Test-Role"].ToString() switch
            {
                "reader" => new[] { new Claim("groups", "reader-group") },
                "operator" => new[] { new Claim("groups", "operator-group") },
                "image-builder" =>
                [
                    new Claim("azp", "image-builder-client"),
                    new Claim("idtyp", "app")
                ],
                "appid-image-builder" =>
                [
                    new Claim("appid", "image-builder-client"),
                    new Claim("idtyp", "app")
                ],
                "wrong-image-builder" =>
                [
                    new Claim("azp", "other-client"),
                    new Claim("idtyp", "app")
                ],
                "missing-idtyp-image-builder" =>
                [
                    new Claim("azp", "image-builder-client")
                ],
                "scoped-image-builder" =>
                [
                    new Claim("azp", "image-builder-client"),
                    new Claim("idtyp", "app"),
                    new Claim("scp", "image.result")
                ],
                "delegated-image-builder" =>
                [
                    new Claim("azp", "image-builder-client"),
                    new Claim("idtyp", "user"),
                    new Claim("scp", "image.result")
                ],
                _ => null
            };
            if (claims is null)
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var principal = new ClaimsPrincipal(
                new ClaimsIdentity(claims, "Test"));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, "Test")));
        }
    }
}
