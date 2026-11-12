namespace Aegis.Studio.Web.SessionPlanning;

public sealed record SubjectPlan(long Revision, string Category, PlanGroupLevel[] Groups, string Role, bool IdentityConfirmed, PlannedShot[] Shots, Dictionary<string, string>? Identification = null, string SubjectKind = "Individual", PlanIdentityField[]? IdentificationFields = null);
