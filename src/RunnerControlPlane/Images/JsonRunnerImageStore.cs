using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace RunnerControlPlane.Images;

public class JsonRunnerImageStore : IRunnerImageStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, RunnerImage> _images = new(StringComparer.Ordinal);
    private readonly object _writeLock = new();
    private readonly string? _filePath;

    public JsonRunnerImageStore()
    {
    }

    public JsonRunnerImageStore(
        IOptions<RunnerImageBuildOptions> options,
        IHostEnvironment environment)
    {
        var path = options.Value.StateFilePath;
        _filePath = string.IsNullOrWhiteSpace(path)
            ? null
            : Path.GetFullPath(path, environment.ContentRootPath);

        if (_filePath is not null && File.Exists(_filePath))
        {
            Load(File.ReadAllText(_filePath));
        }
    }

    public IReadOnlyList<RunnerImage> List()
    {
        return _images.Values
            .OrderByDescending(image => image.RequestedAt)
            .ToArray();
    }

    public IReadOnlyList<RunnerImage> ListAvailable()
    {
        return List()
            .Where(image => image.Status == RunnerImageBuildStatus.Complete)
            .ToArray();
    }

    public RunnerImage? Get(string version)
    {
        return _images.GetValueOrDefault(version);
    }

    public RunnerImage RegisterBuild(RunnerImageBuildRequest request, string manifestDigest, string actor)
    {
        var version = $"ubuntu-{request.UbuntuRelease}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        var image = new RunnerImage(
            version,
            DateTimeOffset.UtcNow,
            request.Expedited,
            RunnerImageSnapshot.Freeze(request),
            RunnerImageBuildStatus.Building,
            null,
            null,
            manifestDigest,
            AuditTrail:
            [
                new RunnerImageBuildAuditRecord(
                    actor,
                    DateTimeOffset.UtcNow,
                    "register-build",
                    "registered")
            ]);
        lock (_writeLock)
        {
            if (!_images.TryAdd(version, image))
            {
                throw new InvalidOperationException($"Runner image build ID '{version}' already exists.");
            }

            Persist();
        }

        return image;
    }

    public RunnerImageBuildResultSubmission ReportBuildResult(
        string version,
        RunnerImageBuildResult result,
        string actor)
    {
        lock (_writeLock)
        {
            if (!_images.TryGetValue(version, out var current))
            {
                return new(RunnerImageBuildResultStatus.NotFound, null, "The runner image build was not found.");
            }

            var submission = EvaluateBuildResult(current, result, actor);
            if (submission.Status == RunnerImageBuildResultStatus.Recorded)
            {
                _images[version] = submission.Image!;
                Persist();
            }

            return submission;
        }
    }

    internal static RunnerImageBuildResultSubmission EvaluateBuildResult(
        RunnerImage current,
        RunnerImageBuildResult result,
        string actor)
    {
        if (current.ManifestDigest is null)
        {
            return new(RunnerImageBuildResultStatus.Invalid, current, "This runner image was not registered for an external build.");
        }

        if (!current.ManifestDigest.Equals(result.ManifestDigest, StringComparison.OrdinalIgnoreCase))
        {
            return new(RunnerImageBuildResultStatus.ManifestMismatch, current, "The result manifest digest does not match the registered build.");
        }

        var inventoryFailure = RunnerImageInventoryValidator.Validate(current.BuildRequest, result.Inventory);
        if (inventoryFailure is not null)
        {
            return new(RunnerImageBuildResultStatus.InventoryMismatch, current, inventoryFailure);
        }

        var frozenResult = Freeze(result);
        if (current.BuildResult is not null)
        {
            var status = AreEquivalent(current.BuildResult, frozenResult)
                ? RunnerImageBuildResultStatus.Idempotent
                : RunnerImageBuildResultStatus.ConflictingRetry;
            return new(
                status,
                current,
                status == RunnerImageBuildResultStatus.ConflictingRetry
                    ? "A conflicting result was already recorded for this build."
                    : null);
        }

        var succeeded = result.Succeeded && result.Validation.IsSuccessful;
        if (succeeded && string.IsNullOrWhiteSpace(result.AzureImageId))
        {
            return new(RunnerImageBuildResultStatus.Invalid, current, "A successful result must include an Azure image identifier.");
        }

        var failureReason = succeeded
            ? null
            : FirstNonEmpty(result.FailureReason, result.Validation.FailureReason)
                ?? "The image failed one or more required validation checks.";
        var updated = current with
        {
            Status = succeeded ? RunnerImageBuildStatus.Complete : RunnerImageBuildStatus.Failed,
            Inventory = frozenResult.Inventory,
            AzureImageId = string.IsNullOrWhiteSpace(frozenResult.AzureImageId)
                ? null
                : frozenResult.AzureImageId,
            FailureReason = failureReason,
            BuildResult = frozenResult,
            AuditTrail = (current.AuditTrail ?? [])
                .Append(new RunnerImageBuildAuditRecord(
                    actor,
                    DateTimeOffset.UtcNow,
                    "report-result",
                    succeeded ? "succeeded" : "failed"))
                .ToArray()
        };
        return new(RunnerImageBuildResultStatus.Recorded, updated, null);
    }

    public RunnerImage AddQueued(string version, RunnerImageBuildRequest request)
    {
        var image = new RunnerImage(
            version,
            DateTimeOffset.UtcNow,
            request.Expedited,
            request,
            RunnerImageBuildStatus.Queued,
            null,
            null);
        lock (_writeLock)
        {
            if (!_images.TryAdd(version, image))
            {
                throw new InvalidOperationException($"Runner image version '{version}' already exists.");
            }

            Persist();
        }

        return image;
    }

    public RunnerImage Add(RunnerImage image)
    {
        lock (_writeLock)
        {
            if (!_images.TryAdd(image.Version, image))
            {
                throw new InvalidOperationException($"Runner image version '{image.Version}' already exists.");
            }

            Persist();
        }

        return image;
    }

    public void Update(string version, Func<RunnerImage, RunnerImage> update)
    {
        lock (_writeLock)
        {
            _images.AddOrUpdate(
                version,
                _ => throw new KeyNotFoundException($"Runner image version '{version}' was not found."),
                (_, image) => update(image));
            Persist();
        }
    }

    private static RunnerImageBuildResult Freeze(RunnerImageBuildResult result)
    {
        return result with { Inventory = RunnerImageSnapshot.Freeze(result.Inventory) };
    }

    private static bool AreEquivalent(RunnerImageBuildResult first, RunnerImageBuildResult second)
    {
        return first.ManifestDigest.Equals(second.ManifestDigest, StringComparison.OrdinalIgnoreCase)
            && string.Equals(first.AzureImageId, second.AzureImageId, StringComparison.OrdinalIgnoreCase)
            && first.Succeeded == second.Succeeded
            && first.FailureReason == second.FailureReason
            && first.Validation == second.Validation
            && first.Inventory.UbuntuRelease.Equals(second.Inventory.UbuntuRelease, StringComparison.OrdinalIgnoreCase)
            && first.Inventory.RunnerVersion.Equals(second.Inventory.RunnerVersion, StringComparison.OrdinalIgnoreCase)
            && new HashSet<string>(first.Inventory.DotNetSdks, StringComparer.OrdinalIgnoreCase)
                .SetEquals(second.Inventory.DotNetSdks)
            && first.Inventory.Packages.Count == second.Inventory.Packages.Count
            && first.Inventory.Packages.All(package =>
                second.Inventory.Packages.TryGetValue(package.Key, out var value)
                && value.Equals(package.Value, StringComparison.OrdinalIgnoreCase));
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    internal static IReadOnlyList<RunnerImage> ReadState(string json)
    {
        try
        {
            var state = JsonSerializer.Deserialize<RunnerImageStoreState>(json, JsonOptions);
            if (state?.Images is null
                || state.Images.Any(image => string.IsNullOrWhiteSpace(image.Version) || image.BuildRequest is null)
                || state.Images.Select(image => image.Version).Distinct(StringComparer.Ordinal).Count() != state.Images.Count)
            {
                throw new InvalidDataException("The saved runner image state is invalid.");
            }

            return state.Images;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The saved runner image state is invalid JSON.", exception);
        }
    }

    private void Load(string json)
    {
        foreach (var image in ReadState(json))
        {
            _images.TryAdd(image.Version, image);
        }
    }

    private void Persist()
    {
        if (_filePath is null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("The runner image state file must have a parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var state = new RunnerImageStoreState(List());
            File.WriteAllBytes(temporaryPath, JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions));
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed record RunnerImageStoreState(IReadOnlyList<RunnerImage> Images);
}
