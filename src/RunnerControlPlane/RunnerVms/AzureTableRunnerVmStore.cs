using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using Azure.Data.Tables;

namespace RunnerControlPlane.RunnerVms;

public sealed class AzureTableRunnerVmStore(TableClient table) : IRunnerVmStore
{
    private const string Partition = "runner-vms";
    private const string ImportPartition = "runner-vm-import";
    private const string ImportedAuditPartition = "runner-vm-imported-audit";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private sealed record State(RunnerVmRecord Vm, IReadOnlyList<RunnerVmAuditRecord> Audit);

    public IReadOnlyList<RunnerVmRecord> List()
    {
        EnsureImportComplete();
        return Rows().Select(entity => Read(entity)).Select(state => state.Vm)
            .OrderByDescending(vm => vm.CreatedAt ?? DateTimeOffset.MinValue).ToArray();
    }

    public RunnerVmRecord? Get(string id)
    {
        EnsureImportComplete();
        return ReadEntity(id) is { } entity ? Read(entity, id).Vm : null;
    }

    public RunnerVmRecord Create(string? imageVersion, string label, string runnerGroup)
    {
        EnsureImportComplete();
        var now = DateTimeOffset.UtcNow;
        var vm = new RunnerVmRecord($"vm-{Guid.NewGuid():N}", imageVersion ?? "latest", label,
            RunnerVmStatus.Idle, JobStatus: "idle", JobId: $"job-{Guid.NewGuid():N}",
            CreatedAt: now, UpdatedAt: now, RunnerGroup: runnerGroup);
        table.AddEntity(Entity(new State(vm, [])));
        return vm;
    }

    public RunnerVmRecord Update(string id, Func<RunnerVmRecord, RunnerVmRecord> update) =>
        Change(id, state => state with { Vm = update(state.Vm) });

    public RunnerVmRecord Retire(string id, string outcome, string? message = null) =>
        Retire(id, outcome, "system", message, "manual-retire");

    public RunnerVmRecord Retire(string id, string outcome, string actor, string? message, string action) =>
        Change(id, state =>
        {
            var normalized = outcome ?? "Success";
            var now = DateTimeOffset.UtcNow;
            var vm = state.Vm with
            {
                Status = RunnerVmStatus.Finished,
                JobStatus = "finished",
                Outcome = normalized,
                IsRetired = true,
                RetryScheduled = false,
                AcceptingJobs = false,
                FailureReason = message,
                UpdatedAt = now
            };
            var audit = new RunnerVmAuditRecord(actor, now, action,
                InMemoryRunnerVmStore.NormalizeAuditOutcome(normalized, action));
            return new State(vm, [.. state.Audit, audit]);
        });

    public IReadOnlyList<RunnerVmAuditRecord> GetAuditTrail()
    {
        EnsureImportComplete();
        var imported = table.Query<TableEntity>(filter: "PartitionKey eq 'runner-vm-imported-audit'")
            .Select(entity => JsonSerializer.Deserialize<RunnerVmAuditRecord>(entity.GetString("Audit")
                ?? throw new InvalidDataException("Imported Runner VM audit row has no payload."), JsonOptions)
                ?? throw new InvalidDataException("Imported Runner VM audit row is invalid."));
        return Rows().Select(entity => Read(entity)).SelectMany(state => state.Audit)
            .Concat(imported).OrderBy(audit => audit.Timestamp).ToArray();
    }

    // All writers must be stopped before snapshots are taken and until import completes.
    public void ImportJson(string recordsJson, string auditJson)
    {
        var records = JsonSerializer.Deserialize<RunnerVmRecord[]>(recordsJson, JsonOptions)
            ?? throw new InvalidDataException("Runner VM snapshot is invalid.");
        var audits = JsonSerializer.Deserialize<RunnerVmAuditRecord[]>(auditJson, JsonOptions)
            ?? throw new InvalidDataException("Runner VM audit snapshot is invalid.");
        if (records.Any(vm => vm is null || string.IsNullOrWhiteSpace(vm.Id)
                || string.IsNullOrWhiteSpace(vm.ImageVersion) || string.IsNullOrWhiteSpace(vm.Label)
                || !Enum.IsDefined(vm.Status))
            || records.Select(vm => vm.Id).Distinct(StringComparer.Ordinal).Count() != records.Length
            || audits.Any(audit => audit is null || string.IsNullOrWhiteSpace(audit.Actor)
                || string.IsNullOrWhiteSpace(audit.Action) || string.IsNullOrWhiteSpace(audit.Outcome)))
            throw new InvalidDataException("Runner VM snapshot contains invalid or duplicate records.");
        var entities = records.Select(vm => Entity(new State(vm, []))).ToArray();
        var auditEntities = audits.Select(audit => new TableEntity(ImportedAuditPartition, Guid.NewGuid().ToString("N"))
        {
            ["Audit"] = SerializeAudit(audit)
        }).ToArray();
        var marker = new TableEntity(ImportPartition, "guard");
        table.AddEntity(marker);
        var guard = table.GetEntity<TableEntity>(ImportPartition, "guard").Value;
        var writing = false;
        try
        {
            if (Rows().Any()
                || table.Query<TableEntity>(filter: "PartitionKey eq 'runner-vm-imported-audit'").Any())
                throw new InvalidOperationException("Runner VM import requires an empty table partition.");
            foreach (var entity in entities.Concat(auditEntities))
            {
                writing = true;
                table.AddEntity(entity);
            }
            table.DeleteEntity(ImportPartition, "guard", guard.ETag);
        }
        catch (Exception exception)
        {
            if (!writing)
            {
                table.DeleteEntity(ImportPartition, "guard", guard.ETag);
                throw;
            }
            throw new InvalidOperationException("Runner VM import may be partial; the store is blocked until reconciliation.", exception);
        }
    }

    private RunnerVmRecord Change(string id, Func<State, State> change)
    {
        EnsureImportComplete();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var entity = ReadEntity(id) ?? throw new KeyNotFoundException($"Runner VM '{id}' was not found.");
            var updated = change(Read(entity, id));
            if (updated.Vm.Id != id)
                throw new InvalidOperationException("Updating a runner VM cannot change its ID.");
            try
            {
                table.UpdateEntity(Entity(updated), entity.ETag, TableUpdateMode.Replace);
                return updated.Vm;
            }
            catch (RequestFailedException exception) when (exception.Status == 412) { }
        }
        throw new InvalidOperationException($"Runner VM '{id}' could not be updated due to concurrent changes.");
    }

    private IEnumerable<TableEntity> Rows() => table.Query<TableEntity>(filter: "PartitionKey eq 'runner-vms'");

    private void EnsureImportComplete()
    {
        try
        {
            table.GetEntity<TableEntity>(ImportPartition, "guard");
            throw new InvalidOperationException("Runner VM import requires reconciliation before the store can be used.");
        }
        catch (RequestFailedException exception) when (exception.Status == 404 && exception.ErrorCode is "ResourceNotFound" or "EntityNotFound") { }
    }

    private TableEntity? ReadEntity(string id)
    {
        try { return table.GetEntity<TableEntity>(Partition, Key(id)).Value; }
        catch (RequestFailedException exception) when (exception.Status == 404 && exception.ErrorCode is "ResourceNotFound" or "EntityNotFound") { return null; }
    }

    private static TableEntity Entity(State state)
    {
        var json = JsonSerializer.Serialize(state, JsonOptions);
        if (Encoding.Unicode.GetByteCount(json) > 64 * 1024)
            throw new InvalidDataException("Runner VM state exceeds the Azure Table string property limit.");
        return new TableEntity(Partition, Key(state.Vm.Id)) { ["State"] = json };
    }

    private static State Read(TableEntity entity, string? id = null)
    {
        var state = JsonSerializer.Deserialize<State>(entity.GetString("State")
            ?? throw new InvalidDataException("Runner VM row has no state."), JsonOptions)
            ?? throw new InvalidDataException("Runner VM row has invalid state.");
        if (state.Vm is null || state.Audit is null || string.IsNullOrWhiteSpace(state.Vm.Id)
            || entity.PartitionKey != Partition || entity.RowKey != Key(state.Vm.Id)
            || (id is not null && state.Vm.Id != id))
            throw new InvalidDataException("Runner VM row has invalid state or key.");
        return state;
    }

    private static string Key(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));

    private static string SerializeAudit(RunnerVmAuditRecord audit)
    {
        var json = JsonSerializer.Serialize(audit, JsonOptions);
        if (Encoding.Unicode.GetByteCount(json) > 64 * 1024)
            throw new InvalidDataException("Runner VM audit exceeds the Azure Table string property limit.");
        return json;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
