using Aegis.Studio.Web.Persistence;

namespace Aegis.Studio.Web.Production;

public sealed class ProductionStore
{
    private readonly OperationalWorkStore activities;
    private readonly ProductionConfigurationStore configuration;

    public ProductionStore(StudioDatabase database)
    {
        activities = new OperationalWorkStore(database);
        configuration = new ProductionConfigurationStore(database);
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
        ProductionRecipeDefinition? recipe=configuration.GetRecipes(true).FirstOrDefault(x=>string.Equals(x.Name,templateName,StringComparison.OrdinalIgnoreCase));
        if(recipe is null)
        {
            JobTypeDefinition? type=configuration.GetJobTypes(true).FirstOrDefault(x=>string.Equals(x.Name,templateName,StringComparison.OrdinalIgnoreCase)
                || string.Equals(x.Name.Replace(" ",""),templateName.Replace(" ",""),StringComparison.OrdinalIgnoreCase));
            if(type?.DefaultRecipeId is Guid recipeId)recipe=configuration.GetRecipes(true).FirstOrDefault(x=>x.Id==recipeId);
        }
        if(recipe is null)throw new InvalidOperationException("No active configurable Production Recipe matches that template name.");
        configuration.GenerateTasks(jobId,"compatibility",recipe.Id);
        return GetForJob(jobId);
    }

    private static ProductionTask ToTask(JobActivity value)
        => new(value.Id,value.JobId,value.Title,value.Description,value.Status,value.AssignedTo,value.DueAtUtc,0,value.CreatedAtUtc,value.CompletedAtUtc);
}

public sealed record ProductionTask(Guid Id,Guid JobId,string Title,string? Description,string Status,string? AssignedTo,DateTimeOffset? DueAtUtc,int SortOrder,DateTimeOffset CreatedAtUtc,DateTimeOffset? CompletedAtUtc);
public sealed record ProductionTaskRequest(string Title,string? Description,string? Status,string? AssignedTo,DateTimeOffset? DueAtUtc,int SortOrder=0);
