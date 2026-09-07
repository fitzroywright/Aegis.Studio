using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Pos;

public sealed class PosAuditStore
{
    private readonly StudioDatabase database;

    public PosAuditStore(StudioDatabase database)
    {
        this.database = database;
    }

    public void Record(string action, Guid saleId, Guid jobId, string userId, decimal amount, string? detail = null)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "insert into studio_pos_audit(id,action,sale_id,job_id,user_id,amount,detail,created_at_utc) values ($1,$2,$3,$4,$5,$6,$7,$8)";
        command.Parameters.AddWithValue(Guid.NewGuid()); command.Parameters.AddWithValue(action); command.Parameters.AddWithValue(saleId); command.Parameters.AddWithValue(jobId); command.Parameters.AddWithValue(userId); command.Parameters.AddWithValue(amount); command.Parameters.AddWithValue((object?)detail ?? DBNull.Value); command.Parameters.AddWithValue(DateTimeOffset.UtcNow); command.ExecuteNonQuery();
    }

    public IReadOnlyList<PosAuditEntry> GetRecent(int count = 100)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id,action,sale_id,job_id,user_id,amount,detail,created_at_utc from studio_pos_audit order by created_at_utc desc limit $1";
        command.Parameters.AddWithValue(Math.Clamp(count,1,500));
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<PosAuditEntry> values=[];
        while(reader.Read()) values.Add(new PosAuditEntry(reader.GetGuid(0),reader.GetString(1),reader.GetGuid(2),reader.GetGuid(3),reader.GetString(4),reader.GetDecimal(5),reader.IsDBNull(6)?null:reader.GetString(6),reader.GetFieldValue<DateTimeOffset>(7)));
        return values;
    }
}

public sealed record PosAuditEntry(Guid Id,string Action,Guid SaleId,Guid JobId,string UserId,decimal Amount,string? Detail,DateTimeOffset CreatedAtUtc);
