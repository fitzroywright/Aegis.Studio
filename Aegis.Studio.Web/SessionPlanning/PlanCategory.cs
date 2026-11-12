namespace Aegis.Studio.Web.SessionPlanning;

public sealed record PlanCategory(string Name, string SubjectKind, string[] GroupingLevels, PlanIdentityField[] IdentificationFields, PlanPhotoRequirement[] PhotoRequirements);
