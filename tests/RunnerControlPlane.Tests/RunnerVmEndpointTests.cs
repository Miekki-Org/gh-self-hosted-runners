using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    public async Task TableStoreSharesUpdatesAndAtomicRetirementAuditAcrossHostsAndRestart()
    {
        var table = new TestTableClient();
        using var firstFactory = new RunnerVmControlPlaneFactory(store: new AzureTableRunnerVmStore(table));
        using var secondFactory = new RunnerVmControlPlaneFactory(store: new AzureTableRunnerVmStore(table));
        using var first = firstFactory.CreateClient();
        using var second = secondFactory.CreateClient();
        first.DefaultRequestHeaders.Add("X-Test-Role", "operator");
        second.DefaultRequestHeaders.Add("X-Test-Role", "operator");
        var vm = await (await first.PostAsJsonAsync("/api/runner-vms", new CreateRunnerVmRequest("24.04")))
            .Content.ReadFromJsonAsync<RunnerVmRecord>(JsonOptions);
        Assert.NotNull(vm);
        table.BeforeNextUpdate = () =>
        {
            var response = second.PostAsJsonAsync($"/api/runner-vms/{vm.Id}/health-checks",
                new RunnerVmHealthCheckRequest(false, true, "heartbeat")).GetAwaiter().GetResult();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        };
        var patch = await first.PatchAsJsonAsync($"/api/runner-vms/{vm.Id}",
            new UpdateRunnerVmStatusRequest(RunnerVmStatus.Working));
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var observed = await second.GetFromJsonAsync<RunnerVmRecord>($"/api/runner-vms/{vm.Id}", JsonOptions);
        Assert.Equal(RunnerVmStatus.Working, observed!.Status);
        Assert.Equal(1, observed.VmHealthFailures);

        Assert.Equal(HttpStatusCode.OK, (await first.PatchAsJsonAsync($"/api/runner-vms/{vm.Id}",
            new UpdateRunnerVmStatusRequest(RunnerVmStatus.Idle))).StatusCode);
        table.FailNextUpdate = true;
        var failure = await first.PostAsync($"/api/runner-vms/{vm.Id}/retire?outcome=Success", null);
        Assert.False(failure.IsSuccessStatusCode);
        Assert.Empty((await second.GetFromJsonAsync<RunnerVmAuditRecord[]>("/api/runner-vms/audit", JsonOptions))!);
        Assert.False((await second.GetFromJsonAsync<RunnerVmRecord>($"/api/runner-vms/{vm.Id}", JsonOptions))!.IsRetired);
        Assert.Equal(HttpStatusCode.OK, (await second.PostAsync($"/api/runner-vms/{vm.Id}/retire?outcome=Success", null)).StatusCode);

        using var restartFactory = new RunnerVmControlPlaneFactory(store: new AzureTableRunnerVmStore(table));
        using var restarted = restartFactory.CreateClient();
        restarted.DefaultRequestHeaders.Add("X-Test-Role", "reader");
        Assert.True((await restarted.GetFromJsonAsync<RunnerVmRecord>($"/api/runner-vms/{vm.Id}", JsonOptions))!.IsRetired);
        var audit = Assert.Single((await restarted.GetFromJsonAsync<RunnerVmAuditRecord[]>("/api/runner-vms/audit", JsonOptions))!);
        Assert.Equal("manual-retire", audit.Action);
        Assert.Equal("succeeded", audit.Outcome);
    }

    [Fact]
    public async Task MissingVmHealthCheckDoesNotBlockLaterOperations()
    {
        var table = new TestTableClient();
        using var factory = new RunnerVmControlPlaneFactory(store: new AzureTableRunnerVmStore(table),
            operationLock: new AzureTableRunnerVmOperationLock(table));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");
        var missing = await client.PostAsJsonAsync("/api/runner-vms/missing/health-checks",
            new RunnerVmHealthCheckRequest(false, false));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var response = await client.PostAsJsonAsync("/api/runner-vms", new CreateRunnerVmRequest("24.04"));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var vm = await response.Content.ReadFromJsonAsync<RunnerVmRecord>(JsonOptions);
        Assert.NotNull(vm);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/runner-vms/{vm.Id}/health-checks",
            new RunnerVmHealthCheckRequest(false, false))).StatusCode);
    }

    [Fact]
    public async Task SnapshotImportRejectsOverwriteAndFailsClosedAfterPartialImport()
    {
        var source = new TestTableClient();
        using var sourceFactory = new RunnerVmControlPlaneFactory(store: new AzureTableRunnerVmStore(source));
        using var client = sourceFactory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");
        var vm = await (await client.PostAsJsonAsync("/api/runner-vms", new CreateRunnerVmRequest("24.04")))
            .Content.ReadFromJsonAsync<RunnerVmRecord>(JsonOptions);
        Assert.NotNull(vm);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/runner-vms/{vm.Id}/retire", null)).StatusCode);
        var records = await client.GetStringAsync("/api/runner-vms");
        var audit = await client.GetStringAsync("/api/runner-vms/audit");
        var target = new TestTableClient();
        var store = new AzureTableRunnerVmStore(target);
        store.ImportJson(records, audit);
        using var targetFactory = new RunnerVmControlPlaneFactory(store: store);
        using var targetClient = targetFactory.CreateClient();
        targetClient.DefaultRequestHeaders.Add("X-Test-Role", "reader");
        Assert.True((await targetClient.GetFromJsonAsync<RunnerVmRecord>($"/api/runner-vms/{vm.Id}", JsonOptions))!.IsRetired);
        Assert.Single((await targetClient.GetFromJsonAsync<RunnerVmAuditRecord[]>("/api/runner-vms/audit", JsonOptions))!);
        Assert.Throws<InvalidOperationException>(() => store.ImportJson(records, audit));
        Assert.Throws<InvalidOperationException>(() => store.ImportJson("[]", "[]"));
        Assert.True((await targetClient.GetFromJsonAsync<RunnerVmRecord>($"/api/runner-vms/{vm.Id}", JsonOptions))!.IsRetired);

        var partial = new TestTableClient { FailAddAfter = 2 };
        var partialStore = new AzureTableRunnerVmStore(partial);
        Assert.Throws<InvalidOperationException>(() => partialStore.ImportJson(records, audit));
        using var partialFactory = new RunnerVmControlPlaneFactory(store: partialStore);
        using var partialClient = partialFactory.CreateClient();
        partialClient.DefaultRequestHeaders.Add("X-Test-Role", "reader");
        var error = await Record.ExceptionAsync(async () =>
        {
            var response = await partialClient.GetAsync("/api/runner-vms");
            Assert.False(response.IsSuccessStatusCode);
        });
        Assert.True(error is null or InvalidOperationException);
    }

    [Fact]
    public async Task SimultaneousHealthChecksAcrossHostsCannotBothEnterVmOperation()
    {
        var table = new TestTableClient();
        using var firstFactory = new RunnerVmControlPlaneFactory(store: new AzureTableRunnerVmStore(table),
            operationLock: new AzureTableRunnerVmOperationLock(table));
        using var secondFactory = new RunnerVmControlPlaneFactory(store: new AzureTableRunnerVmStore(table),
            operationLock: new AzureTableRunnerVmOperationLock(table));
        using var first = firstFactory.CreateClient();
        using var second = secondFactory.CreateClient();
        first.DefaultRequestHeaders.Add("X-Test-Role", "operator");
        second.DefaultRequestHeaders.Add("X-Test-Role", "operator");
        var vm = await (await first.PostAsJsonAsync("/api/runner-vms", new CreateRunnerVmRequest("24.04")))
            .Content.ReadFromJsonAsync<RunnerVmRecord>(JsonOptions);
        Assert.NotNull(vm);
        table.BeforeNextUpdate = () =>
        {
            var error = Record.Exception(() =>
            {
                var response = second.PostAsJsonAsync($"/api/runner-vms/{vm.Id}/health-checks",
                    new RunnerVmHealthCheckRequest(false, false)).GetAwaiter().GetResult();
                Assert.False(response.IsSuccessStatusCode);
            });
            Assert.True(error is null or InvalidOperationException);
        };
        var response = await first.PostAsJsonAsync($"/api/runner-vms/{vm.Id}/health-checks",
            new RunnerVmHealthCheckRequest(false, false));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = await second.GetFromJsonAsync<RunnerVmRecord>($"/api/runner-vms/{vm.Id}", JsonOptions);
        Assert.Equal(1, after!.VmHealthFailures);
        Assert.Equal(1, after.RunnerConnectivityFailures);
        Assert.Equal(HttpStatusCode.OK, (await second.PostAsJsonAsync($"/api/runner-vms/{vm.Id}/health-checks",
            new RunnerVmHealthCheckRequest(false, false))).StatusCode);
    }

    [Fact]
    public async Task UncertainReplacementKeepsGlobalOperationLockAcrossHosts()
    {
        var table = new TestTableClient();
        var capacity = new ReplacementCapacity();
        using var firstFactory = new RunnerVmControlPlaneFactory(store: new AzureTableRunnerVmStore(table),
            operationLock: new AzureTableRunnerVmOperationLock(table), capacity: capacity);
        using var secondFactory = new RunnerVmControlPlaneFactory(store: new AzureTableRunnerVmStore(table),
            operationLock: new AzureTableRunnerVmOperationLock(table), capacity: capacity);
        using var first = firstFactory.CreateClient();
        using var second = secondFactory.CreateClient();
        first.DefaultRequestHeaders.Add("X-Test-Role", "operator");
        second.DefaultRequestHeaders.Add("X-Test-Role", "operator");
        var vm = await (await first.PostAsJsonAsync("/api/runner-vms", new CreateRunnerVmRequest("24.04")))
            .Content.ReadFromJsonAsync<RunnerVmRecord>(JsonOptions);
        Assert.NotNull(vm);
        table.FailNextUpdate = true;
        var failure = await Record.ExceptionAsync(async () =>
        {
            var response = await first.PostAsync($"/api/runner-vms/{vm.Id}/force-remove", null);
            Assert.False(response.IsSuccessStatusCode);
        });
        Assert.True(failure is null or RequestFailedException);
        Assert.Equal(1, capacity.Requests);
        var blocked = await Record.ExceptionAsync(async () =>
        {
            var response = await second.PostAsync($"/api/runner-vms/{vm.Id}/force-remove", null);
            Assert.False(response.IsSuccessStatusCode);
        });
        Assert.True(blocked is null or InvalidOperationException);
        Assert.Equal(1, capacity.Requests);
    }

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

    [Fact]
    public async Task OneHealthCheckFailureDoesNotMarkRunnerVmUnhealthy()
    {
        using var factory = new RunnerVmControlPlaneFactory(healthFailureThreshold: 3);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");

        var created = await client.PostAsJsonAsync("/api/runner-vms", new CreateRunnerVmRequest("24.04"));
        var vm = await created.Content.ReadFromJsonAsync<RunnerVmRecord>(JsonOptions);
        var healthCheck = await client.PostAsJsonAsync(
            $"/api/runner-vms/{vm!.Id}/health-checks",
            new
            {
                vmHealthSucceeded = false,
                runnerConnectivitySucceeded = false,
                vmHealthFailureReason = "Guest heartbeat timed out.",
                runnerConnectivityFailureReason = "Runner is offline in GitHub."
            });
        var result = await healthCheck.Content.ReadFromJsonAsync<RunnerVmRecord>(JsonOptions);

        Assert.Equal(HttpStatusCode.OK, healthCheck.StatusCode);
        Assert.False(result!.IsUnhealthy);
        Assert.True(result.AcceptingJobs);
        Assert.Equal(1, result.VmHealthFailures);
        Assert.Equal(1, result.RunnerConnectivityFailures);
        Assert.Contains("Guest heartbeat timed out.", result.HealthFailureReason);
        Assert.Contains("Runner is offline in GitHub.", result.HealthFailureReason);
    }

    [Fact]
    public async Task RepeatedFailureOfOnlyOneCheckDoesNotMarkRunnerVmUnhealthy()
    {
        using var factory = new RunnerVmControlPlaneFactory(healthFailureThreshold: 3);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");

        var created = await client.PostAsJsonAsync("/api/runner-vms", new CreateRunnerVmRequest("24.04"));
        var vm = await created.Content.ReadFromJsonAsync<RunnerVmRecord>(JsonOptions);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var healthCheck = await client.PostAsJsonAsync(
                $"/api/runner-vms/{vm!.Id}/health-checks",
                new RunnerVmHealthCheckRequest(
                    false,
                    true,
                    "Guest heartbeat timed out."));
            Assert.Equal(HttpStatusCode.OK, healthCheck.StatusCode);
        }

        var result = await client.GetFromJsonAsync<RunnerVmRecord>($"/api/runner-vms/{vm!.Id}", JsonOptions);

        Assert.False(result!.IsUnhealthy);
        Assert.True(result.AcceptingJobs);
        Assert.Equal(3, result.VmHealthFailures);
        Assert.Equal(0, result.RunnerConnectivityFailures);
        Assert.Equal(RunnerVmStatus.Idle, result.Status);
    }

    [Fact]
    public async Task RepeatedFailuresStopAssignmentsAndReplaceTheVmAfterItsJobDrains()
    {
        using var factory = new RunnerVmControlPlaneFactory(maximumCapacity: 3, healthFailureThreshold: 3);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");

        var created = await client.PostAsJsonAsync("/api/runner-vms", new CreateRunnerVmRequest("24.04"));
        var vm = await created.Content.ReadFromJsonAsync<RunnerVmRecord>(JsonOptions);
        var started = await client.PatchAsJsonAsync(
            $"/api/runner-vms/{vm!.Id}",
            new UpdateRunnerVmStatusRequest(RunnerVmStatus.Working));
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var healthCheck = await client.PostAsJsonAsync(
                $"/api/runner-vms/{vm.Id}/health-checks",
                new RunnerVmHealthCheckRequest(
                    false,
                    false,
                    "Guest heartbeat timed out.",
                    "Runner is offline in GitHub."));
            Assert.Equal(HttpStatusCode.OK, healthCheck.StatusCode);
        }

        var unhealthy = await client.GetFromJsonAsync<RunnerVmRecord>($"/api/runner-vms/{vm.Id}", JsonOptions);
        var beforeDrain = await client.GetFromJsonAsync<RunnerVmRecord[]>("/api/runner-vms", JsonOptions);
        Assert.True(unhealthy!.IsUnhealthy);
        Assert.False(unhealthy.AcceptingJobs);
        Assert.Equal(RunnerVmStatus.Working, unhealthy.Status);
        Assert.False(unhealthy.IsRetired);
        Assert.Single(beforeDrain!);

        await client.PostAsJsonAsync(
            $"/api/runner-vms/{vm.Id}/health-checks",
            new RunnerVmHealthCheckRequest(true, true));
        var stillUnhealthy = await client.GetFromJsonAsync<RunnerVmRecord>($"/api/runner-vms/{vm.Id}", JsonOptions);
        Assert.Equal(unhealthy.HealthFailureReason, stillUnhealthy!.HealthFailureReason);
        var assignment = await client.PatchAsJsonAsync(
            $"/api/runner-vms/{vm.Id}",
            new UpdateRunnerVmStatusRequest(RunnerVmStatus.Working));
        Assert.Equal(HttpStatusCode.Conflict, assignment.StatusCode);

        var finished = await client.PatchAsJsonAsync(
            $"/api/runner-vms/{vm.Id}",
            new UpdateRunnerVmStatusRequest(RunnerVmStatus.Finished, "Success"));
        var retired = await finished.Content.ReadFromJsonAsync<RunnerVmRecord>(JsonOptions);
        var runnerVms = await client.GetFromJsonAsync<RunnerVmRecord[]>("/api/runner-vms", JsonOptions);
        var audit = await client.GetFromJsonAsync<RunnerVmAuditRecord[]>("/api/runner-vms/audit", JsonOptions);
        var capacity = await client.GetFromJsonAsync<CapacitySnapshot>("/api/runner-capacity");

        Assert.Equal(HttpStatusCode.OK, finished.StatusCode);
        Assert.True(retired!.IsRetired);
        Assert.False(retired.RetryScheduled);
        Assert.Equal("Success", retired.Outcome);
        Assert.Equal(2, runnerVms!.Length);
        Assert.Equal("automatic-unhealthy-retire", audit![0].Action);
        Assert.Equal("system", audit[0].Actor);
        Assert.Equal("succeeded", audit[0].Outcome);
        Assert.InRange(capacity!.CurrentCapacity, 0, capacity.MaximumCapacity);
    }

    [Fact]
    public async Task OperatorCanForceRemoveWorkingVmAndCapacityRequestRespectsMaximum()
    {
        using var factory = new RunnerVmControlPlaneFactory(maximumCapacity: 1);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");

        var created = await client.PostAsJsonAsync("/api/runner-vms", new CreateRunnerVmRequest("24.04"));
        var vm = await created.Content.ReadFromJsonAsync<RunnerVmRecord>(JsonOptions);
        await client.PatchAsJsonAsync(
            $"/api/runner-vms/{vm!.Id}",
            new UpdateRunnerVmStatusRequest(RunnerVmStatus.Working));

        var forceRemove = await client.PostAsync($"/api/runner-vms/{vm.Id}/force-remove", null);
        var removed = await forceRemove.Content.ReadFromJsonAsync<RunnerVmRecord>(JsonOptions);
        var audit = await client.GetFromJsonAsync<RunnerVmAuditRecord[]>("/api/runner-vms/audit", JsonOptions);
        var capacityAudit = await client.GetFromJsonAsync<CapacityAuditRecord[]>("/api/runner-capacity/audit");
        var capacity = await client.GetFromJsonAsync<CapacitySnapshot>("/api/runner-capacity");
        var removalAudit = Assert.Single(audit!);
        var scaleOutAudit = Assert.Single(capacityAudit!);

        Assert.Equal(HttpStatusCode.OK, forceRemove.StatusCode);
        Assert.True(removed!.IsRetired);
        Assert.False(removed.RetryScheduled);
        Assert.Equal("replacement-capped", removed.Outcome);
        Assert.Equal("force-remove", removalAudit.Action);
        Assert.Equal("Capacity Test Operator", removalAudit.Actor);
        Assert.NotEqual(default, removalAudit.Timestamp);
        Assert.Contains("capped", scaleOutAudit.Outcome, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, capacity!.CurrentCapacity);
        Assert.Equal(1, capacity.MaximumCapacity);
    }

    [Fact]
    public async Task StatusUpdateCannotRetireRunnerVmWithoutAnAuditedRemovalAction()
    {
        using var factory = new RunnerVmControlPlaneFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");

        var created = await client.PostAsJsonAsync("/api/runner-vms", new CreateRunnerVmRequest("24.04"));
        var vm = await created.Content.ReadFromJsonAsync<RunnerVmRecord>(JsonOptions);
        var update = await client.PatchAsJsonAsync(
            $"/api/runner-vms/{vm!.Id}",
            new UpdateRunnerVmStatusRequest(RunnerVmStatus.Finished, IsRetired: true));
        var current = await client.GetFromJsonAsync<RunnerVmRecord>($"/api/runner-vms/{vm.Id}", JsonOptions);
        var audit = await client.GetFromJsonAsync<RunnerVmAuditRecord[]>("/api/runner-vms/audit", JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
        Assert.False(current!.IsRetired);
        Assert.Empty(audit!);
    }

    [Fact]
    public async Task SystemRetiresIdleVmAfterGracePeriodWhileKeepingWarmPoolMinimum()
    {
        using var factory = new RunnerVmControlPlaneFactory(
            minimumCapacity: 1,
            idleGracePeriodSeconds: 0,
            reconcileIntervalSeconds: 1);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");

        var first = await client.PostAsJsonAsync("/api/runner-vms", new CreateRunnerVmRequest("24.04"));
        var second = await client.PostAsJsonAsync("/api/runner-vms", new CreateRunnerVmRequest("24.04"));
        var firstVm = await first.Content.ReadFromJsonAsync<RunnerVmRecord>(JsonOptions);
        var secondVm = await second.Content.ReadFromJsonAsync<RunnerVmRecord>(JsonOptions);

        var store = factory.Services.GetRequiredService<IRunnerVmStore>();
        store.Update(firstVm!.Id, current => current with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-10) });
        store.Update(secondVm!.Id, current => current with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-10) });

        var cleanup = factory.Services.GetRequiredService<IRunnerVmCleanupService>();
        var retired = await cleanup.RetireIdleVmsAsync();
        var audit = await client.GetFromJsonAsync<RunnerVmAuditRecord[]>($"/api/runner-vms/audit", JsonOptions);
        var auditRecord = Assert.Single(audit!);

        Assert.Equal(1, retired);
        Assert.Equal("system", auditRecord.Actor);
        Assert.Equal("automatic-idle-retire", auditRecord.Action);
        Assert.Contains("retired", auditRecord.Outcome, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OperatorCanManuallyRetireIdleVmEvenWhenItDropsBelowMinimum()
    {
        using var factory = new RunnerVmControlPlaneFactory(minimumCapacity: 2, idleGracePeriodSeconds: 600);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");

        var created = await client.PostAsJsonAsync("/api/runner-vms", new CreateRunnerVmRequest("24.04"));
        var vm = await created.Content.ReadFromJsonAsync<RunnerVmRecord>(JsonOptions);
        var retirement = await client.PostAsync($"/api/runner-vms/{vm!.Id}/retire?outcome=Success&actor=operator", null);
        var record = await retirement.Content.ReadFromJsonAsync<RunnerVmRecord>(JsonOptions);
        var audit = await client.GetFromJsonAsync<RunnerVmAuditRecord[]>($"/api/runner-vms/audit", JsonOptions);
        var auditRecord = Assert.Single(audit!);

        Assert.Equal(HttpStatusCode.OK, retirement.StatusCode);
        Assert.Equal(RunnerVmStatus.Finished, record!.Status);
        Assert.True(record.IsRetired);
        Assert.Equal("Success", record.Outcome);
        Assert.Equal("operator", auditRecord.Actor);
        Assert.Equal("manual-retire", auditRecord.Action);
        Assert.Equal("succeeded", auditRecord.Outcome);
    }

    private sealed class RunnerVmControlPlaneFactory(
        int minimumCapacity = 1,
        int maximumCapacity = 10,
        int idleGracePeriodSeconds = 300,
        int reconcileIntervalSeconds = 3600,
        int healthFailureThreshold = 3,
        IRunnerVmStore? store = null,
        IRunnerVmOperationLock? operationLock = null,
        IRunnerCapacityController? capacity = null) : WebApplicationFactory<Program>
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
                    ["Runner:Label"] = "linux-dotnet",
                    ["RunnerCapacity:MinimumCapacity"] = minimumCapacity.ToString(),
                    ["RunnerCapacity:MaximumCapacity"] = maximumCapacity.ToString(),
                    ["RunnerCapacity:IdleGracePeriodSeconds"] = idleGracePeriodSeconds.ToString(),
                    ["RunnerCapacity:ReconcileIntervalSeconds"] = reconcileIntervalSeconds.ToString(),
                    ["RunnerVm:HealthFailureThreshold"] = healthFailureThreshold.ToString()
                });
            });

            builder.ConfigureTestServices(services =>
            {
                if (store is not null)
                {
                    services.RemoveAll<IRunnerVmStore>();
                    services.AddSingleton(store);
                }
                if (operationLock is not null)
                {
                    services.RemoveAll<IRunnerVmOperationLock>();
                    services.AddSingleton(operationLock);
                }
                if (capacity is not null)
                {
                    services.RemoveAll<IRunnerCapacityController>();
                    services.AddSingleton(capacity);
                }
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

    public sealed class TestTableClient : TableClient
    {
        private readonly Dictionary<(string, string), TableEntity> _rows = new();
        private readonly object _gate = new();
        private int _revision;
        public Action? BeforeNextUpdate { get; set; }
        public bool FailNextUpdate { get; set; }
        public int? FailAddAfter { get; set; }

        public override Response AddEntity<T>(T entity, CancellationToken cancellationToken = default)
        {
            if (FailAddAfter is int remaining)
            {
                if (remaining == 0) throw new RequestFailedException(500, "Injected add failure");
                FailAddAfter = remaining - 1;
            }
            var row = (TableEntity)(object)entity;
            lock (_gate)
            {
                if (_rows.ContainsKey((row.PartitionKey, row.RowKey)))
                    throw new RequestFailedException(409, "Already exists");
                _rows.Add((row.PartitionKey, row.RowKey), Copy(row, new ETag((++_revision).ToString())));
            }
            return new TestResponse();
        }

        public override Response<T> GetEntity<T>(string partitionKey, string rowKey,
            IEnumerable<string>? select = null, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (!_rows.TryGetValue((partitionKey, rowKey), out var row))
                    throw new RequestFailedException(404, "Not found", "ResourceNotFound", null);
                return Response.FromValue((T)(object)Copy(row, row.ETag), new TestResponse());
            }
        }

        public override Response UpdateEntity<T>(T entity, ETag ifMatch,
            TableUpdateMode mode = TableUpdateMode.Merge, CancellationToken cancellationToken = default)
        {
            var callback = BeforeNextUpdate;
            BeforeNextUpdate = null;
            callback?.Invoke();
            if (FailNextUpdate)
            {
                FailNextUpdate = false;
                throw new RequestFailedException(500, "Injected failure");
            }

            var row = (TableEntity)(object)entity;
            lock (_gate)
            {
                if (!_rows.TryGetValue((row.PartitionKey, row.RowKey), out var current))
                    throw new RequestFailedException(404, "Not found", "ResourceNotFound", null);
                if (current.ETag != ifMatch)
                    throw new RequestFailedException(412, "Precondition failed");
                _rows[(row.PartitionKey, row.RowKey)] = Copy(row, new ETag((++_revision).ToString()));
            }

            return new TestResponse();
        }

        public override Response DeleteEntity(string partitionKey, string rowKey, ETag ifMatch = default,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (!_rows.TryGetValue((partitionKey, rowKey), out var row))
                    throw new RequestFailedException(404, "Not found", "ResourceNotFound", null);
                if (row.ETag != ifMatch)
                    throw new RequestFailedException(412, "Precondition failed");
                _rows.Remove((partitionKey, rowKey));
            }
            return new TestResponse();
        }

        public override Pageable<T> Query<T>(string? filter = null, int? maxPerPage = null,
            IEnumerable<string>? select = null, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                var partition = filter?.Split('\'')[1];
                var rows = _rows.Values.Where(row => row.PartitionKey == partition)
                    .Select(row => (T)(object)Copy(row, row.ETag)).ToArray();
                return Pageable<T>.FromPages([Page<T>.FromValues(rows, null, new TestResponse())]);
            }
        }

        private static TableEntity Copy(TableEntity row, ETag etag)
        {
            var copy = new TableEntity(row.PartitionKey, row.RowKey) { ETag = etag };
            foreach (var property in row)
                copy[property.Key] = property.Value;
            return copy;
        }
    }

    private sealed class ReplacementCapacity : IRunnerCapacityController
    {
        public int Requests { get; private set; }
        private static readonly CapacitySnapshot Snapshot = new(0, 10, 0, 0, 0, 0, 0, 0, false);
        public Task<CapacitySnapshot> GetSnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(Snapshot);
        public Task<CapacitySnapshot> UpdateSettingsAsync(int minimumCapacity, int maximumCapacity, string actor, CancellationToken cancellationToken) => Task.FromResult(Snapshot);
        public Task<ScaleOutResult> RequestScaleOutAsync(int count, string actor, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new ScaleOutResult(count, 1, false, Snapshot));
        }
        public Task<CapacitySnapshot> ReconcileAsync(CancellationToken cancellationToken) => Task.FromResult(Snapshot);
        public IReadOnlyList<CapacityAuditRecord> GetAuditTrail() => [];
    }

    private sealed class TestResponse : Response
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
                new ClaimsIdentity(
                    [new Claim("groups", group), new Claim("name", "Capacity Test Operator")],
                    "Test"));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, "Test")));
        }
    }
}
