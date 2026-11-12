namespace Aegis.Studio.Web.SessionPlanning;

public sealed record ApplyPlanTemplate(Guid? TemplateId, long TemplateRevision, long Revision, PlanTemplate? Definition = null);
