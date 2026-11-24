using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;

namespace RunnerControlPlane.Images;

public sealed class AzureTableRunnerImageStore(TableClient table) : IRunnerImageStore
{
    private const string Partition = "runner-images";
    private const int MaxConcurrencyAttempts = 20;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<RunnerImage> List() =>
        table.Query<TableEntity>(filter: "PartitionKey eq 'runner-images'")
            .Select(entity => ReadImage(entity))
            .OrderByDescending(image => image.RequestedAt)
            .ToArray();

    public IReadOnlyList<RunnerImage> ListAvailable() =>
        List().Where(image => image.Status == RunnerImageBuildStatus.Complete).ToArray();

    public RunnerImage? Get(string version)
    {
        var entity = ReadEntity(version);
        return entity is null ? null : ReadImage(entity, version);
    }

    public RunnerImage RegisterBuild(RunnerImageBuildRequest request, string manifestDigest, string actor)
    {
        var version = $"ubuntu-{request.UbuntuRelease}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        var image = new RunnerImage(
            version, DateTimeOffset.UtcNow, request.Expedited, RunnerImageSnapshot.Freeze(request),
            RunnerImageBuildStatus.Building, null, null, manifestDigest,
            AuditTrail: [new RunnerImageBuildAuditRecord(actor, DateTimeOffset.UtcNow, "register-build", "registered")]);
        Insert(image, $"Runner image build ID '{version}' already exists.");
        return image;
    }

    public RunnerImage AddQueued(string version, RunnerImageBuildRequest request)
    {
        var image = new RunnerImage(version, DateTimeOffset.UtcNow, request.Expedited, request,
            RunnerImageBuildStatus.Queued, null, null);
        Insert(image, $"Runner image version '{version}' already exists.");
        return image;
    }

    public RunnerImageBuildResultSubmission ReportBuildResult(
        string version, RunnerImageBuildResult result, string actor)
    {
        for (var attempt = 0; attempt < MaxConcurrencyAttempts; attempt++)
        {
            var entity = ReadEntity(version);
            if (entity is null)
            {
                return new(RunnerImageBuildResultStatus.NotFound, null, "The runner image build was not found.");
            }

            var submission = JsonRunnerImageStore.EvaluateBuildResult(ReadImage(entity, version), result, actor);
            if (submission.Status != RunnerImageBuildResultStatus.Recorded)
            {
                return submission;
            }

            try
            {
                table.UpdateEntity(CreateEntity(submission.Image!), entity.ETag, TableUpdateMode.Replace);
                return submission;
            }
            catch (RequestFailedException exception) when (exception.Status is 404 or 412)
            {
                // Re-read and re-evaluate the result against the winner's state.
            }
        }

        throw new InvalidOperationException($"Runner image version '{version}' could not be updated due to concurrent changes.");
    }

    public void Update(string version, Func<RunnerImage, RunnerImage> update)
    {
        for (var attempt = 0; attempt < MaxConcurrencyAttempts; attempt++)
        {
            var entity = ReadEntity(version)
                ?? throw new KeyNotFoundException($"Runner image version '{version}' was not found.");
            var updated = update(ReadImage(entity, version));
            if (!string.Equals(updated.Version, version, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Updating a runner image cannot change its version.");
            }

            try
            {
                table.UpdateEntity(CreateEntity(updated), entity.ETag, TableUpdateMode.Replace);
                return;
            }
            catch (RequestFailedException exception) when (exception.Status is 404 or 412)
            {
                // Reapply the update to the latest state instead of overwriting concurrent changes.
            }
        }

        throw new InvalidOperationException($"Runner image version '{version}' could not be updated due to concurrent changes.");
    }

    /// <summary>
    /// Explicitly copies a JSON store into the provisioned table without replacing any rows.
    /// Stop JSON writers before calling. The file is parsed and conflicts are
    /// checked before writing, but inserts are not transactional: a failure or concurrent
    /// importer can leave a partial import. Reconcile or remove those rows before retrying;
    /// no existing row, even an identical one, is silently skipped.
    /// </summary>
    public void ImportJsonFile(string path)
    {
        var images = JsonRunnerImageStore.ReadState(File.ReadAllText(path));
        if (images.Any(image => image.ManifestDigest is null
            && image.Status is RunnerImageBuildStatus.Queued
                or RunnerImageBuildStatus.Building
                or RunnerImageBuildStatus.Validating))
        {
            throw new InvalidDataException(
                "The image state contains unfinished local builds. Complete or reconcile them before migration.");
        }

        var entities = images.Select(CreateEntity).ToArray();
        foreach (var image in images)
        {
            if (ReadEntity(image.Version) is not null)
            {
                throw new InvalidOperationException($"Runner image version '{image.Version}' already exists; no images were imported.");
            }
        }

        foreach (var entity in entities)
        {
            try
            {
                table.AddEntity(entity);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "Runner image import failed and may be partial; reconcile the table before retrying.", exception);
            }
        }
    }

    private void Insert(RunnerImage image, string conflictMessage)
    {
        try
        {
            table.AddEntity(CreateEntity(image));
        }
        catch (RequestFailedException exception) when (exception.Status == 409)
        {
            throw new InvalidOperationException(conflictMessage, exception);
        }
    }

    private TableEntity? ReadEntity(string version)
    {
        try
        {
            return table.GetEntity<TableEntity>(Partition, RowKey(version)).Value;
        }
        catch (RequestFailedException exception) when (
            exception.Status == 404 && exception.ErrorCode is "ResourceNotFound" or "EntityNotFound")
        {
            return null;
        }
    }

    private static TableEntity CreateEntity(RunnerImage image)
    {
        var json = JsonSerializer.Serialize(image, JsonOptions);
        if (Encoding.Unicode.GetByteCount(json) > 64 * 1024)
        {
            throw new InvalidDataException($"Runner image version '{image.Version}' exceeds the Azure Table string property limit.");
        }

        return new TableEntity(Partition, RowKey(image.Version)) { ["Image"] = json };
    }

    private static RunnerImage ReadImage(TableEntity entity, string? expectedVersion = null)
    {
        var image = JsonSerializer.Deserialize<RunnerImage>(entity.GetString("Image")
            ?? throw new InvalidDataException("A runner image row has no image payload."), JsonOptions)
            ?? throw new InvalidDataException("A runner image row has an invalid image payload.");
        if (string.IsNullOrWhiteSpace(image.Version) || image.BuildRequest is null
            || entity.PartitionKey != Partition || entity.RowKey != RowKey(image.Version)
            || (expectedVersion is not null && image.Version != expectedVersion))
        {
            throw new InvalidDataException("A runner image row does not match its key or has an invalid payload.");
        }

        return image with
        {
            BuildRequest = RunnerImageSnapshot.Freeze(image.BuildRequest),
            Inventory = image.Inventory is null ? null : RunnerImageSnapshot.Freeze(image.Inventory),
            BuildResult = image.BuildResult is null ? null : image.BuildResult with
            {
                Inventory = RunnerImageSnapshot.Freeze(image.BuildResult.Inventory)
            }
        };
    }

    private static string RowKey(string version) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(version)));
}
