using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.WorkDay;
namespace Aegis.Studio.Web.Production;
// Compatibility adapter. WorkDay owns schedule writes; Studio embeds the manager workflow.
public sealed class ShootCoordinator(StudioDatabase database,PhotographerStore photographers)
{
    public ShootSession Create(Guid jobId,IntegratedShootRequest request,string actor)
        =>new WorkDaySchedulingService(database,photographers).Create(jobId,request,actor);
}
public sealed record IntegratedShootRequest(string PhotographerUserName,string? AssistantUserName,DateTimeOffset CallTimeUtc,DateTimeOffset PlannedEndUtc,string? Location,string? CoverageNotes,string? EquipmentNotes,IReadOnlyList<Guid>? ResourceIds);
