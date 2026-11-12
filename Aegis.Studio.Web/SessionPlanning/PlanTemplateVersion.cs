namespace Aegis.Studio.Web.SessionPlanning;

public sealed record PlanTemplateVersion(Guid Id, long Revision, bool Active, PlanTemplate Definition);
