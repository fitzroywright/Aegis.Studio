using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Production;

public sealed class ProductionStore
{
    private readonly StudioDatabase database;

    public ProductionStore(StudioDatabase database)
    {
        this.database = database;
    }

    public IReadOnlyList<ProductionTask> GetForJob(Guid jobId)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id,job_id,title,description,status,assigned_to,due_at_utc,sort_order,created_at_utc,completed_at_utc from studio_production_tasks where job_id=$1 order by sort_order,created_at_utc";
        command.Parameters.AddWithValue(jobId);
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<ProductionTask> values=[];
        while(reader.Read()) values.Add(Read(reader));
        return values;
    }

    public IReadOnlyList<ProductionTask> GetQueue(string? status = null)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            select id,job_id,title,description,status,assigned_to,due_at_utc,sort_order,created_at_utc,completed_at_utc
            from studio_production_tasks
            where ($1::text is null or status=$1)
            order by case when due_at_utc is null then 1 else 0 end,due_at_utc,created_at_utc
            """;
        command.Parameters.AddWithValue((object?)status ?? DBNull.Value);
        using NpgsqlDataReader reader=command.ExecuteReader();
        List<ProductionTask> values=[];
        while(reader.Read()) values.Add(Read(reader));
        return values;
    }

    public ProductionTask Create(Guid jobId, ProductionTaskRequest request)
    {
        if(string.IsNullOrWhiteSpace(request.Title)) throw new InvalidOperationException("Task title is required.");
        ProductionTask task=new(Guid.NewGuid(),jobId,request.Title.Trim(),request.Description?.Trim(),NormalizeStatus(request.Status),request.AssignedTo?.Trim(),request.DueAtUtc?.ToUniversalTime(),request.SortOrder,DateTimeOffset.UtcNow,null);
        Save(task);
        return task;
    }

    public ProductionTask Update(Guid id, ProductionTaskRequest request)
    {
        ProductionTask current=GetById(id) ?? throw new KeyNotFoundException("Production task was not found.");
        string status=NormalizeStatus(request.Status);
        DateTimeOffset? completed=status=="Completed" ? current.CompletedAtUtc ?? DateTimeOffset.UtcNow : null;
        ProductionTask updated=current with { Title=request.Title.Trim(),Description=request.Description?.Trim(),Status=status,AssignedTo=request.AssignedTo?.Trim(),DueAtUtc=request.DueAtUtc?.ToUniversalTime(),SortOrder=request.SortOrder,CompletedAtUtc=completed };
        Save(updated);
        return updated;
    }

    public IReadOnlyList<ProductionTask> ApplyTemplate(Guid jobId, string templateName)
    {
        if (GetForJob(jobId).Count > 0) return GetForJob(jobId);
        IReadOnlyList<string> titles = templateName.Trim().ToLowerInvariant() switch
        {
            "graduation" => ["Import and back up images","Cull and select images","Color correction","Retouch selected images","Prepare proofs","Client selection review","Final export","Archive deliverables"],
            "id" or "id photos" => ["Import and back up images","Match images to subjects","Crop to specification","Quality check","Export final files","Archive deliverables"],
            _ => ["Import and back up images","Cull and select images","Edit and color correct","Retouch selected images","Client proof review","Final export","Delivery quality check","Archive deliverables"]
        };
        int order=0;
        foreach(string title in titles) Create(jobId,new ProductionTaskRequest(title,null,"NotStarted",null,null,order++));
        return GetForJob(jobId);
    }

    private ProductionTask? GetById(Guid id)
    {
        using NpgsqlConnection connection=database.OpenConnection();using NpgsqlCommand command=connection.CreateCommand();command.CommandText="select id,job_id,title,description,status,assigned_to,due_at_utc,sort_order,created_at_utc,completed_at_utc from studio_production_tasks where id=$1";command.Parameters.AddWithValue(id);using NpgsqlDataReader reader=command.ExecuteReader();return reader.Read()?Read(reader):null;
    }

    private void Save(ProductionTask task)
    {
        using NpgsqlConnection connection=database.OpenConnection();using NpgsqlCommand command=connection.CreateCommand();command.CommandText="""
            insert into studio_production_tasks(id,job_id,title,description,status,assigned_to,due_at_utc,sort_order,created_at_utc,completed_at_utc)
            values($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)
            on conflict(id) do update set title=excluded.title,description=excluded.description,status=excluded.status,assigned_to=excluded.assigned_to,due_at_utc=excluded.due_at_utc,sort_order=excluded.sort_order,completed_at_utc=excluded.completed_at_utc
            """;command.Parameters.AddWithValue(task.Id);command.Parameters.AddWithValue(task.JobId);command.Parameters.AddWithValue(task.Title);command.Parameters.AddWithValue((object?)task.Description??DBNull.Value);command.Parameters.AddWithValue(task.Status);command.Parameters.AddWithValue((object?)task.AssignedTo??DBNull.Value);command.Parameters.AddWithValue((object?)task.DueAtUtc??DBNull.Value);command.Parameters.AddWithValue(task.SortOrder);command.Parameters.AddWithValue(task.CreatedAtUtc);command.Parameters.AddWithValue((object?)task.CompletedAtUtc??DBNull.Value);command.ExecuteNonQuery();
    }

    private static string NormalizeStatus(string? value) => value?.Trim() switch { "InProgress"=>"InProgress","Blocked"=>"Blocked","Completed"=>"Completed",_=>"NotStarted" };
    private static ProductionTask Read(NpgsqlDataReader r)=>new(r.GetGuid(0),r.GetGuid(1),r.GetString(2),r.IsDBNull(3)?null:r.GetString(3),r.GetString(4),r.IsDBNull(5)?null:r.GetString(5),r.IsDBNull(6)?null:r.GetFieldValue<DateTimeOffset>(6),r.GetInt32(7),r.GetFieldValue<DateTimeOffset>(8),r.IsDBNull(9)?null:r.GetFieldValue<DateTimeOffset>(9));
}

public sealed record ProductionTask(Guid Id,Guid JobId,string Title,string? Description,string Status,string? AssignedTo,DateTimeOffset? DueAtUtc,int SortOrder,DateTimeOffset CreatedAtUtc,DateTimeOffset? CompletedAtUtc);
public sealed record ProductionTaskRequest(string Title,string? Description,string? Status,string? AssignedTo,DateTimeOffset? DueAtUtc,int SortOrder=0);
