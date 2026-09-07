using Aegis.Studio.Web.Persistence;

namespace Aegis.Studio.Web.Production;

public sealed class ProductionStore
{
    private readonly OperationalWorkStore activities;

    public ProductionStore(StudioDatabase database)
    {
        activities = new OperationalWorkStore(database);
    }

    public IReadOnlyList<ProductionTask> GetForJob(Guid jobId)
        => activities.GetActivities(jobId).Select(ToTask).ToList();

    public IReadOnlyList<ProductionTask> GetQueue(string? status = null)
        => activities.GetActivities(null,status,null).Select(ToTask).ToList();

    public ProductionTask Create(Guid jobId, ProductionTaskRequest request)
    {
        JobActivity created = activities.CreateActivity(jobId,new JobActivityRequest("Other",request.Title,request.Description,request.Status,"Normal",request.AssignedTo,null,request.DueAtUtc,null,null,null),"compatibility");
        return ToTask(created);
    }

    public ProductionTask Update(Guid id, ProductionTaskRequest request)
    {
        JobActivity current = activities.GetActivities().FirstOrDefault(x=>x.Id==id) ?? throw new KeyNotFoundException("Production task was not found.");
        JobActivity updated = activities.UpdateActivity(id,new JobActivityRequest(current.ActivityType,request.Title,request.Description,request.Status,current.Priority,request.AssignedTo,current.ScheduledAtUtc,request.DueAtUtc,current.Location,current.BlockedReason,current.HandoffTo),"compatibility");
        return ToTask(updated);
    }

    public IReadOnlyList<ProductionTask> ApplyTemplate(Guid jobId, string templateName)
    {
        if (activities.GetActivities(jobId).Count == 0)
        {
            (string Type,string Title)[] steps = templateName.Trim().ToLowerInvariant() switch
            {
                "graduation" => [("FileIngest","Import images"),("Backup","Verify backup"),("Cull","Cull images"),("Edit","Color correction"),("Retouch","Retouch selections"),("Proof","Prepare proofs"),("QC","Quality control"),("Delivery","Final export and delivery"),("Archive","Archive deliverables")],
                "id" or "id photos" => [("FileIngest","Import images"),("Backup","Verify backup"),("Edit","Match and crop to specification"),("QC","Quality check"),("Delivery","Export final files"),("Archive","Archive deliverables")],
                _ => [("FileIngest","Import images"),("Backup","Verify backup"),("Cull","Cull and select"),("Edit","Edit and color correct"),("Retouch","Retouch selections"),("Proof","Client proof review"),("QC","Final quality control"),("Delivery","Deliver final files"),("Archive","Archive deliverables")]
            };
            foreach ((string type,string title) in steps)
            {
                activities.CreateActivity(jobId,new JobActivityRequest(type,title,null,"NotStarted","Normal",null,null,null,null,null,null),"compatibility");
            }
        }
        return GetForJob(jobId);
    }

    private static ProductionTask ToTask(JobActivity value)
        => new(value.Id,value.JobId,value.Title,value.Description,value.Status,value.AssignedTo,value.DueAtUtc,0,value.CreatedAtUtc,value.CompletedAtUtc);
}

public sealed record ProductionTask(Guid Id,Guid JobId,string Title,string? Description,string Status,string? AssignedTo,DateTimeOffset? DueAtUtc,int SortOrder,DateTimeOffset CreatedAtUtc,DateTimeOffset? CompletedAtUtc);
public sealed record ProductionTaskRequest(string Title,string? Description,string? Status,string? AssignedTo,DateTimeOffset? DueAtUtc,int SortOrder=0);
