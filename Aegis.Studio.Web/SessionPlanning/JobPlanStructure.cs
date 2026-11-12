namespace Aegis.Studio.Web.SessionPlanning;

public sealed record JobPlanStructure(long Revision, Guid? TemplateId, long? TemplateRevision, PlanTemplate Definition);
