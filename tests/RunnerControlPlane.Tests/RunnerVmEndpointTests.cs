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

public sealed class RunnerVmEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task ReaderCanInspectRunnerVmsButCannotRequestOne()
    {
        using var factory = new RunnerVmControlPlaneFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "reader");

        var vms = await client.GetFromJsonAsync<RunnerVmRecord[]>($"/api/runner-vms", JsonOptions);
        var request = await client.PostAsJsonAsync("/api/runner-vms", new CreateRunnerVmRequest("24.04"));

        Assert.NotNull(vms);
        Assert.Empty(vms!);
        Assert.Equal(HttpStatusCode.Forbidden, request.StatusCode);
    }

    [Fact]
    public async Task OperatorCanProvisionAndRetireOneRunnerVmForAQueuedJob()
    {
        using var factory = new RunnerVmControlPlaneFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");

        var created = await client.PostAsJsonAsync("/api/runner-vms", new CreateRunnerVmRequest("24.04"));
        var vm = await created.Content.ReadFromJsonAsync<RunnerVmRecord>(JsonOptions);
        var details = await client.GetFromJsonAsync<RunnerVmRecord>($"/api/runner-vms/{vm!.Id}", JsonOptions);

        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        Assert.Equal("24.04", vm.ImageVersion);
        Assert.Equal("linux-dotnet", vm.Label);
        Assert.Equal(RunnerVmStatus.Idle, details!.Status);

        var completed = await client.PostAsync($"/api/runner-vms/{vm.Id}/retire?outcome=Success", null);
        var retired = await completed.Content.ReadFromJsonAsync<RunnerVmRecord>(JsonOptions);

        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal(RunnerVmStatus.Finished, retired!.Status);
        Assert.True(retired.IsRetired);
        Assert.Equal("Success", retired.Outcome);
        Assert.False(retired.RetryScheduled);
    }

    [Fact]
    public async Task FailedAndCancelledJobsAreVisibleAndNeverRetriedAutomatically()
    {
        using var factory = new RunnerVmControlPlaneFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");

        var created = await client.PostAsJsonAsync("/api/runner-vms", new CreateRunnerVmRequest("24.04"));
        var vm = await created.Content.ReadFromJsonAsync<RunnerVmRecord>(JsonOptions);

        var failed = await client.PostAsync($"/api/runner-vms/{vm!.Id}/retire?outcome=Failed&error=The%20job%20failed.", null);
        var failure = await failed.Content.ReadFromJsonAsync<RunnerVmRecord>(JsonOptions);
        var cancelled = await client.PostAsJsonAsync("/api/runner-vms", new CreateRunnerVmRequest("24.04"));
        var secondVm = await cancelled.Content.ReadFromJsonAsync<RunnerVmRecord>(JsonOptions);
        var cancelledOutcome = await client.PostAsync($"/api/runner-vms/{secondVm!.Id}/retire?outcome=Cancelled", null);
        var cancelledRecord = await cancelledOutcome.Content.ReadFromJsonAsync<RunnerVmRecord>(JsonOptions);

        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        Assert.Equal("Failed", failure!.Outcome);
        Assert.True(failure.IsRetired);
        Assert.False(failure.RetryScheduled);
        Assert.Equal(HttpStatusCode.OK, cancelledOutcome.StatusCode);
        Assert.Equal("Cancelled", cancelledRecord!.Outcome);
        Assert.False(cancelledRecord.RetryScheduled);
    }

    private sealed class RunnerVmControlPlaneFactory : WebApplicationFactory<Program>
    {
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
                services.RemoveAll<IRunnerImageStore>();
                var imageStore = new InMemoryRunnerImageStore();
                imageStore.Add(new RunnerImage(
                    "24.04",
                    DateTimeOffset.UtcNow,
                    true,
                    new RunnerImageBuildRequest("24.04", ["8.0.404"], new Dictionary<string, string> { ["curl"] = "8.5.0" }, "2.331.0"),
                    RunnerImageBuildStatus.Complete,
                    new RunnerImageInventory("24.04", ["8.0.404"], new Dictionary<string, string> { ["curl"] = "8.5.0" }, "2.331.0"),
                    null));
                services.AddSingleton<IRunnerImageStore>(imageStore);
                services.AddAuthentication()
                    .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("Test", _ => { });
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
