using Azure;
using Azure.Data.Tables;

namespace RunnerControlPlane.RunnerVms;

public interface IRunnerVmOperationLock
{
    Task<IRunnerVmOperationLease> AcquireAsync(CancellationToken cancellationToken);
}

public interface IRunnerVmOperationLease
{
    void Complete();
}

public sealed class InMemoryRunnerVmOperationLock : IRunnerVmOperationLock
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<IRunnerVmOperationLease> AcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_gate.Wait(0))
            throw new InvalidOperationException("Another Runner VM operation is in progress or requires reconciliation.");
        return Task.FromResult<IRunnerVmOperationLease>(new Lease(_gate));
    }

    private sealed class Lease(SemaphoreSlim gate) : IRunnerVmOperationLease
    {
        private int _completed;
        public void Complete()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
                gate.Release();
        }
    }
}

public sealed class AzureTableRunnerVmOperationLock(TableClient table) : IRunnerVmOperationLock
{
    private const string Partition = "runner-vm-operations";
    private const string Row = "global-lock";

    public Task<IRunnerVmOperationLease> AcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var owner = Guid.NewGuid().ToString("N");
        try
        {
            table.AddEntity(new TableEntity(Partition, Row) { ["Owner"] = owner }, cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status == 409)
        {
            throw new InvalidOperationException("Another Runner VM operation is in progress or requires reconciliation.", exception);
        }

        var entity = table.GetEntity<TableEntity>(Partition, Row, cancellationToken: cancellationToken).Value;
        if (entity.GetString("Owner") != owner || entity.ETag == default)
            throw new InvalidOperationException("Runner VM operation lock ownership could not be verified; reconciliation is required.");
        return Task.FromResult<IRunnerVmOperationLease>(new Lease(table, entity.ETag));
    }

    private sealed class Lease(TableClient table, ETag etag) : IRunnerVmOperationLease
    {
        private bool _completed;
        public void Complete()
        {
            if (_completed)
                return;
            table.DeleteEntity(Partition, Row, etag);
            _completed = true;
        }
    }
}
