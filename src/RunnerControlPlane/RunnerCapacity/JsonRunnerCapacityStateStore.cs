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

    public async Task<IRunnerCapacityStateLease> AcquireAsync(
        int defaultMinimumCapacity,
        int defaultMaximumCapacity,
        CapacityAuditRecord? audit,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = Load() ?? new RunnerCapacityState(defaultMinimumCapacity, defaultMaximumCapacity, []);
            if (audit is not null)
            {
                state = state with { AuditTrail = [.. state.AuditTrail, audit] };
                await SaveAsync(state, cancellationToken);
            }

            return new Lease(this, state, audit is not null);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    private async Task SaveAsync(RunnerCapacityState state, CancellationToken cancellationToken)
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

    private sealed class Lease(JsonRunnerCapacityStateStore store, RunnerCapacityState state, bool hasAudit)
        : IRunnerCapacityStateLease
    {
        private bool _disposed;
        public RunnerCapacityState State { get; } = state;

        public async Task CompleteAsync(int minimumCapacity, int maximumCapacity, string? outcome, CancellationToken cancellationToken)
        {
            var audit = State.AuditTrail.ToArray();
            if (hasAudit)
            {
                audit[^1] = audit[^1] with { Outcome = outcome ?? throw new ArgumentNullException(nameof(outcome)) };
            }

            await store.SaveAsync(new RunnerCapacityState(minimumCapacity, maximumCapacity, audit), cancellationToken);
        }

        public Task FailUncertainAsync(CancellationToken cancellationToken) =>
            CompleteAsync(State.MinimumCapacity, State.MaximumCapacity, hasAudit ? "failed" : null, cancellationToken);

        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                store._gate.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
