using System.Text.Json;
using Microsoft.Extensions.Options;

namespace RunnerControlPlane.RunnerCapacity;

public sealed class JsonRunnerCapacityStateStore : IRunnerCapacityStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string? _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RunnerCapacityState? _memoryState;

    public JsonRunnerCapacityStateStore(
        IOptions<RunnerCapacityOptions> options,
        IHostEnvironment environment)
    {
        var path = options.Value.StateFilePath;
        _filePath = string.IsNullOrWhiteSpace(path)
            ? null
            : Path.GetFullPath(path, environment.ContentRootPath);
    }

    public RunnerCapacityState? Load()
    {
        if (_filePath is null)
        {
            return _memoryState;
        }

        if (!File.Exists(_filePath))
        {
            return null;
        }

        try
        {
            var state = JsonSerializer.Deserialize<RunnerCapacityState>(
                File.ReadAllText(_filePath),
                JsonOptions);
            if (state is null
                || state.MinimumCapacity < 0
                || state.MaximumCapacity < 1
                || state.MinimumCapacity > state.MaximumCapacity
                || state.AuditTrail is null)
            {
                throw new InvalidDataException("The saved runner capacity state is invalid.");
            }

            return state;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The saved runner capacity state is invalid JSON.", exception);
        }
    }

    public async Task SaveAsync(RunnerCapacityState state, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_filePath is null)
            {
                _memoryState = state;
                return;
            }

            var directory = Path.GetDirectoryName(_filePath)
                ?? throw new InvalidOperationException("The capacity state file must have a parent directory.");
            Directory.CreateDirectory(directory);
            var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var stream = new FileStream(
                                 temporaryPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 bufferSize: 4096,
                                 useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }

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
        finally
        {
            _gate.Release();
        }
    }
}
