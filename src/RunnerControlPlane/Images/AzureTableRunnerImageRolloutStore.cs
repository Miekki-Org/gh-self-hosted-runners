using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using Azure.Data.Tables;

namespace RunnerControlPlane.Images;

public sealed class AzureTableRunnerImageRolloutStore(TableClient table) : IRunnerImageRolloutStore
{
    private const string Partition = "runner-image-rollouts";
    private const string ImportPartition = "runner-rollout-import";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public IReadOnlyList<RunnerImageRollout> List()
    {
        EnsureImportComplete();
        return Rows().Select(entity => Read(entity)).OrderByDescending(rollout => rollout.RequestedAt).ToArray();
    }

    public RunnerImageRollout? Get(string id)
    {
        EnsureImportComplete();
        return ReadEntity(id) is { } entity ? Read(entity, id) : null;
    }

    public RunnerImageRollout Create(string imageVersion, string? previousImageVersion, string actor)
    {
        EnsureImportComplete();
        var now = DateTimeOffset.UtcNow;
        var rollout = new RunnerImageRollout($"rollout-{Guid.NewGuid():N}", imageVersion, previousImageVersion,
            RunnerImageRolloutStage.Pending, actor, now, now, CurrentDeployedImageVersion: previousImageVersion,
            AuditTrail: [new RunnerImageRolloutAuditRecord(actor, now, "request-rollout", "queued")]);
        table.AddEntity(Entity(rollout));
        return rollout;
    }

    public RunnerImageRollout Update(string id, Func<RunnerImageRollout, RunnerImageRollout> update)
    {
        EnsureImportComplete();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var entity = ReadEntity(id) ?? throw new KeyNotFoundException($"Runner image rollout '{id}' was not found.");
            var existing = Read(entity, id);
            var updated = update(existing);
            if (updated.Id != id)
                throw new InvalidOperationException("Updating a rollout cannot change its ID.");
            if (updated.AuditTrail is null || existing.AuditTrail is null
                || !updated.AuditTrail.Take(existing.AuditTrail.Count).SequenceEqual(existing.AuditTrail))
                throw new InvalidOperationException("Rollout audit history cannot be changed or removed.");
            try
            {
                table.UpdateEntity(Entity(updated), entity.ETag, TableUpdateMode.Replace);
                return updated;
            }
            catch (RequestFailedException exception) when (exception.Status == 412) { }
        }
        throw new InvalidOperationException($"Runner image rollout '{id}' could not be updated due to concurrent changes.");
    }

    public IReadOnlyList<RunnerImageRolloutAuditRecord> GetAuditTrail()
    {
        EnsureImportComplete();
        return Rows().Select(entity => Read(entity)).SelectMany(rollout => rollout.AuditTrail!)
            .OrderBy(audit => audit.Timestamp).ToArray();
    }

    // All writers must be stopped before snapshots are taken and until import completes.
    public void ImportJson(string recordsJson, string auditJson)
    {
        var records = JsonSerializer.Deserialize<RunnerImageRollout[]>(recordsJson, JsonOptions)
            ?? throw new InvalidDataException("Runner image rollout snapshot is invalid.");
        var audits = JsonSerializer.Deserialize<RunnerImageRolloutAuditRecord[]>(auditJson, JsonOptions)
            ?? throw new InvalidDataException("Runner image rollout audit snapshot is invalid.");
        if (records.Any(rollout => rollout is null || string.IsNullOrWhiteSpace(rollout.Id)
                || string.IsNullOrWhiteSpace(rollout.ImageVersion) || string.IsNullOrWhiteSpace(rollout.Actor)
                || !Enum.IsDefined(rollout.Stage) || rollout.AuditTrail is null
                || rollout.AuditTrail.Any(audit => audit is null))
            || records.Select(rollout => rollout.Id).Distinct(StringComparer.Ordinal).Count() != records.Length
            || audits.Any(audit => audit is null || string.IsNullOrWhiteSpace(audit.Actor)
                || string.IsNullOrWhiteSpace(audit.Action) || string.IsNullOrWhiteSpace(audit.Outcome)))
            throw new InvalidDataException("Runner image rollout snapshot contains invalid or duplicate records.");
        var embedded = records.SelectMany(rollout => rollout.AuditTrail!).GroupBy(audit => audit)
            .ToDictionary(group => group.Key, group => group.Count());
        var exported = audits.GroupBy(audit => audit).ToDictionary(group => group.Key, group => group.Count());
        if (embedded.Count != exported.Count
            || embedded.Any(pair => !exported.TryGetValue(pair.Key, out var count) || pair.Value != count))
            throw new InvalidDataException("Runner image rollout and audit snapshots do not match.");
        var entities = records.Select(Entity).ToArray();
        table.AddEntity(new TableEntity(ImportPartition, "guard"));
        var guard = table.GetEntity<TableEntity>(ImportPartition, "guard").Value;
        var writing = false;
        try
        {
            if (Rows().Any())
                throw new InvalidOperationException("Runner image rollout import requires an empty table partition.");
            foreach (var entity in entities)
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
            throw new InvalidOperationException("Runner image rollout import may be partial; the store is blocked until reconciliation.", exception);
        }
    }

    private IEnumerable<TableEntity> Rows() => table.Query<TableEntity>(filter: "PartitionKey eq 'runner-image-rollouts'");

    private void EnsureImportComplete()
    {
        try
        {
            table.GetEntity<TableEntity>(ImportPartition, "guard");
            throw new InvalidOperationException("Runner image rollout import requires reconciliation before the store can be used.");
        }
        catch (RequestFailedException exception) when (exception.Status == 404 && exception.ErrorCode is "ResourceNotFound" or "EntityNotFound") { }
    }

    private TableEntity? ReadEntity(string id)
    {
        try { return table.GetEntity<TableEntity>(Partition, Key(id)).Value; }
        catch (RequestFailedException exception) when (exception.Status == 404 && exception.ErrorCode is "ResourceNotFound" or "EntityNotFound") { return null; }
    }

    private static TableEntity Entity(RunnerImageRollout rollout)
    {
        var json = JsonSerializer.Serialize(rollout, JsonOptions);
        if (Encoding.Unicode.GetByteCount(json) > 64 * 1024)
            throw new InvalidDataException("Runner image rollout exceeds the Azure Table string property limit.");
        return new TableEntity(Partition, Key(rollout.Id)) { ["State"] = json };
    }

    private static RunnerImageRollout Read(TableEntity entity, string? id = null)
    {
        var rollout = JsonSerializer.Deserialize<RunnerImageRollout>(entity.GetString("State")
            ?? throw new InvalidDataException("Runner image rollout row has no state."), JsonOptions)
            ?? throw new InvalidDataException("Runner image rollout row has invalid state.");
        if (string.IsNullOrWhiteSpace(rollout.Id) || rollout.AuditTrail is null
            || entity.PartitionKey != Partition || entity.RowKey != Key(rollout.Id)
            || (id is not null && rollout.Id != id))
            throw new InvalidDataException("Runner image rollout row has invalid state or key.");
        return rollout;
    }

    private static string Key(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
