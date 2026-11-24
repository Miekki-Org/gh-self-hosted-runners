using System.Text.Json;
using Azure;
using Azure.Data.Tables;

namespace RunnerControlPlane.RunnerCapacity;

/// <summary>Persists capacity settings and individual audit records in a provisioned table.</summary>
/// <remarks>
/// The supplied client must use managed identity (or another token credential); this class never
/// creates the table. An interrupted operation leaves its lock in place for operator investigation.
/// </remarks>
public sealed class AzureTableRunnerCapacityStateStore(TableClient table) : IRunnerCapacityStateStore
{
    private const string Partition = "runner-capacity";
    private const string SettingsKey = "settings";
    private const string AuditPrefix = "audit-";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public RunnerCapacityState? Load()
    {
        try
        {
            var settings = ReadSettings();
            if (settings is null)
            {
                if (ReadAudits().Count != 0)
                    throw new InvalidDataException("Capacity audit records exist without settings.");
                return null;
            }
            if (settings.GetString("Owner") == "migration")
                throw new InvalidDataException("Capacity migration is incomplete.");
            return ParseState(settings, ReadAudits());
        }
        catch (Exception ex) when (ex is RequestFailedException or InvalidDataException)
        {
            throw Failure(ex);
        }
    }

    public async Task<IRunnerCapacityStateLease> AcquireAsync(
        int defaultMinimumCapacity, int defaultMaximumCapacity, CapacityAuditRecord? audit,
        CancellationToken cancellationToken)
    {
        try
        {
            var settings = ReadSettings();
            if (settings is null)
            {
                if (ReadAudits().Count != 0)
                    throw new InvalidDataException("Capacity audit records exist without settings.");
                settings = Settings(defaultMinimumCapacity, defaultMaximumCapacity, "");
                try
                {
                    await table.AddEntityAsync(settings, cancellationToken);
                }
                catch (RequestFailedException ex) when (ex.Status == 409)
                {
                    // Another instance initialized the settings; use its current version.
                }
                settings = ReadSettings() ?? throw new InvalidDataException("Capacity settings disappeared.");
            }

            var state = ParseState(settings, []);
            if (!string.IsNullOrEmpty(settings.GetString("Owner")))
                throw new InvalidDataException("Another capacity operation is active or requires recovery.");

            var owner = Guid.NewGuid().ToString("N");
            var locked = Settings(state.MinimumCapacity, state.MaximumCapacity, owner);
            var actions = new List<TableTransactionAction>
            {
                new(TableTransactionActionType.UpdateReplace, locked, settings.ETag)
            };
            TableEntity? auditEntity = null;
            if (audit is not null)
            {
                auditEntity = AuditEntity(audit);
                actions.Add(new TableTransactionAction(TableTransactionActionType.Add, auditEntity));
            }
            await table.SubmitTransactionAsync(actions, cancellationToken);
            var acquired = ReadSettings() ?? throw new InvalidDataException("Capacity settings disappeared after acquisition.");
            if (acquired.GetString("Owner") != owner)
                throw new InvalidDataException("Capacity operation ownership changed.");
            return new Lease(table, state, acquired.ETag, owner, auditEntity);
        }
        catch (Exception ex) when (ex is RequestFailedException or InvalidDataException)
        {
            throw Failure(ex);
        }
    }

    /// <summary>
    /// Explicitly imports a JSON store into an empty partition. Stop all writers first. No table is
    /// created or overwritten. If any step fails after claiming the partition, the migration lock
    /// remains: investigate and clear the entire partition before retrying; never unlock partial data.
    /// </summary>
    public async Task ImportJsonAsync(string path, CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        RunnerCapacityState state;
        try
        {
            state = JsonSerializer.Deserialize<RunnerCapacityState>(json, JsonOptions)
                ?? throw new InvalidDataException("The saved capacity state is empty.");
            Validate(state);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The saved capacity state is invalid JSON.", ex);
        }

        try
        {
            if (table.Query<TableEntity>(filter: $"PartitionKey eq '{Partition}'").Any())
                throw new InvalidDataException("The capacity partition is not empty.");
            await table.AddEntityAsync(Settings(state.MinimumCapacity, state.MaximumCapacity, "migration"), cancellationToken);
            var locked = ReadSettings() ?? throw new InvalidDataException("Migration settings disappeared.");
            // Detect pre-existing orphan records even if another writer raced the initial check.
            if (ReadAudits().Count != 0)
                throw new InvalidDataException("The capacity partition contains audit records.");
            foreach (var audit in state.AuditTrail)
            {
                await table.AddEntityAsync(AuditEntity(audit), cancellationToken);
            }
            await table.UpdateEntityAsync(Settings(state.MinimumCapacity, state.MaximumCapacity, ""),
                locked.ETag, TableUpdateMode.Replace, cancellationToken);
        }
        catch (Exception ex) when (ex is RequestFailedException or InvalidDataException)
        {
            throw Failure(ex);
        }
    }

    private TableEntity? ReadSettings()
    {
        try { return table.GetEntity<TableEntity>(Partition, SettingsKey).Value; }
        catch (RequestFailedException ex) when (ex.Status == 404 && ex.ErrorCode == "ResourceNotFound") { return null; }
    }

    private List<CapacityAuditRecord> ReadAudits() => table.Query<TableEntity>(
            filter: $"PartitionKey eq '{Partition}' and RowKey ge '{AuditPrefix}' and RowKey lt 'audit.'")
        .Select(e => new CapacityAuditRecord(
            e.GetString("Actor") ?? throw new InvalidDataException("Audit actor is missing."),
            e.GetDateTimeOffset("OccurredAt") ?? throw new InvalidDataException("Audit timestamp is missing."),
            e.GetString("Action") ?? throw new InvalidDataException("Audit action is missing."),
            e.GetString("Outcome") ?? throw new InvalidDataException("Audit outcome is missing.")))
        .ToList();

    private static RunnerCapacityState ParseState(TableEntity entity, IReadOnlyList<CapacityAuditRecord> audits)
    {
        var state = new RunnerCapacityState(
            entity.GetInt32("MinimumCapacity") ?? throw new InvalidDataException("Minimum capacity is missing."),
            entity.GetInt32("MaximumCapacity") ?? throw new InvalidDataException("Maximum capacity is missing."), audits);
        Validate(state);
        if (entity.GetString("Owner") is null)
            throw new InvalidDataException("Capacity operation owner is missing.");
        return state;
    }

    private static void Validate(RunnerCapacityState state)
    {
        if (state.MinimumCapacity < 0 || state.MaximumCapacity < 1 || state.MinimumCapacity > state.MaximumCapacity
            || state.AuditTrail is null || state.AuditTrail.Any(a => a is null || a.Actor is null || a.Action is null || a.Outcome is null))
            throw new InvalidDataException("The saved runner capacity state is invalid.");
    }

    private static TableEntity Settings(int min, int max, string owner) => new(Partition, SettingsKey)
    {
        ["MinimumCapacity"] = min, ["MaximumCapacity"] = max, ["Owner"] = owner
    };

    private static TableEntity AuditEntity(CapacityAuditRecord audit) =>
        new(Partition, $"{AuditPrefix}{audit.Timestamp.UtcTicks:D19}-{Guid.NewGuid():N}")
        {
            ["Actor"] = audit.Actor, ["OccurredAt"] = audit.Timestamp,
            ["Action"] = audit.Action, ["Outcome"] = audit.Outcome
        };

    private static RunnerCapacityIntegrationException Failure(Exception ex) =>
        new("Runner capacity table storage failed or is locked.", ex);

    private sealed class Lease(TableClient table, RunnerCapacityState state, ETag etag, string owner, TableEntity? audit)
        : IRunnerCapacityStateLease
    {
        public RunnerCapacityState State { get; } = state;

        public async Task CompleteAsync(int minimumCapacity, int maximumCapacity, string? outcome, CancellationToken cancellationToken)
        {
            var actions = new List<TableTransactionAction>
            {
                new(TableTransactionActionType.UpdateReplace, Settings(minimumCapacity, maximumCapacity, ""), etag)
            };
            if (audit is not null)
            {
                audit["Outcome"] = outcome ?? throw new ArgumentNullException(nameof(outcome));
                // The settings ETag serializes all writers to this audit row.
                actions.Add(new TableTransactionAction(TableTransactionActionType.UpdateReplace, audit, ETag.All));
            }
            try { await table.SubmitTransactionAsync(actions, cancellationToken); }
            catch (RequestFailedException ex) { throw Failure(ex); }
        }

        public async Task FailUncertainAsync(CancellationToken cancellationToken)
        {
            if (audit is null)
                return;
            audit["Outcome"] = "failed";
            try
            {
                await table.SubmitTransactionAsync(
                [
                    new(TableTransactionActionType.UpdateReplace, Settings(State.MinimumCapacity, State.MaximumCapacity, owner), etag),
                    new(TableTransactionActionType.UpdateReplace, audit, ETag.All)
                ], cancellationToken);
            }
            catch (RequestFailedException ex) { throw Failure(ex); }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
