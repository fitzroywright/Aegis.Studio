namespace Aegis.Studio.Web.SessionPlanning;

public sealed record PlanTemplate(string Name, string Description, Guid[] WorkspaceIds, PlanCategory[] Categories);
