using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Operations;

// Reset and generation share one database-wide boundary with requests and workers.
public static class DemoMaintenance
{
    private static readonly AsyncLocal<StudioDatabase?> resetting = new();

    public static IDisposable EnterReset(StudioDatabase database)
    {
        if (ReferenceEquals(resetting.Value, database)) return new EmptyLease();
        var reservation = ReserveReset(database);
        var context = UseResetContext(database);
        return new CombinedLease(reservation, context);
    }

    public static IDisposable ReserveReset(StudioDatabase database)
    {
        var connection = database.OpenConnection();
        try
        {
            using var command = new NpgsqlCommand("select pg_try_advisory_lock(hashtextextended('studio-demo-maintenance:'||current_schema(),0))", connection);
            if (!(bool)command.ExecuteScalar()!)
                throw new InvalidOperationException("Studio is busy or a demo reset is already running. Wait for it to finish before starting another.");
            return new Lease(connection, false, null);
        }
        catch { connection.Dispose(); throw; }
    }

    public static IDisposable UseResetContext(StudioDatabase database)
    {
        var previous = resetting.Value;
        resetting.Value = database;
        return new ContextLease(() => resetting.Value = previous);
    }

    public static IDisposable? TryEnterOperation(StudioDatabase database)
    {
        if (ReferenceEquals(resetting.Value, database)) return new EmptyLease();
        var connection = database.OpenConnection();
        try
        {
            using var command = new NpgsqlCommand("select pg_try_advisory_lock_shared(hashtextextended('studio-demo-maintenance:'||current_schema(),0))", connection);
            if ((bool)command.ExecuteScalar()!) return new Lease(connection, true, null);
            connection.Dispose(); return null;
        }
        catch { connection.Dispose(); throw; }
    }

    private sealed class EmptyLease : IDisposable { public void Dispose() { } }
    private sealed class ContextLease(Action ended) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) ended(); }
    }
    private sealed class CombinedLease(IDisposable reservation, IDisposable context) : IDisposable
    { public void Dispose() { try { reservation.Dispose(); } finally { context.Dispose(); } } }
    private sealed class Lease(NpgsqlConnection connection, bool shared, Action? ended) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try
            {
                using var command = new NpgsqlCommand("select pg_advisory_unlock" + (shared ? "_shared" : "") + "(hashtextextended('studio-demo-maintenance:'||current_schema(),0))", connection);
                command.ExecuteNonQuery();
            }
            finally { connection.Dispose(); ended?.Invoke(); }
        }
    }
}
