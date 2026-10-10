using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Pos;

// Financial stores already commit their own transactions. A session lock serializes
// the complete orchestration without holding an unrelated idle transaction open.
internal sealed class PosOperationLock : IDisposable
{
    private readonly NpgsqlConnection connection;
    private readonly string key;
    public PosOperationLock(StudioDatabase db, string key)
    {
        connection = db.OpenConnection(); this.key = key;
        try { using var q = new NpgsqlCommand("select pg_advisory_lock(hashtextextended($1,0))", connection); q.Parameters.AddWithValue(key); q.ExecuteNonQuery(); }
        catch { connection.Dispose(); throw; }
    }
    public void Dispose()
    {
        try { using var q = new NpgsqlCommand("select pg_advisory_unlock(hashtextextended($1,0))", connection); q.Parameters.AddWithValue(key); q.ExecuteNonQuery(); }
        finally { connection.Dispose(); }
    }
}
