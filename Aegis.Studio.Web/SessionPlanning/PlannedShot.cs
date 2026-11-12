namespace Aegis.Studio.Web.SessionPlanning;

public sealed record PlannedShot(string Name, string Status, Guid[] AssetIds, Guid? ApprovedVersionId = null, string Note = "");
