namespace Aegis.Studio.Web.Media;

/// <summary>Bounds active uploads and requests waiting to ingest, before reading their bodies.</summary>
public sealed class MediaIngestQueue(int concurrency = 2, int waitingCapacity = 8)
{
    private readonly SemaphoreSlim admission = new(concurrency + waitingCapacity, concurrency + waitingCapacity);
    private readonly SemaphoreSlim workers = new(concurrency, concurrency);

    public async Task<IDisposable?> AcquireAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!await admission.WaitAsync(0, cancellationToken)) return null;
        bool acquired = false;
        try
        {
            acquired = await workers.WaitAsync(timeout, cancellationToken);
            return acquired ? new Lease(workers, admission) : null;
        }
        finally
        {
            if (!acquired) admission.Release();
        }
    }

    private sealed class Lease(SemaphoreSlim workers, SemaphoreSlim admission) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            workers.Release();
            admission.Release();
        }
    }
}
