using System.Text.Json;
namespace Aegis.Studio.Web.SessionPlanning;

public static class SubjectPlanRules
{
    public static PlanGroupLevel[] ParseHierarchy(string value)
    {
        if (value.Length > 2000) throw new ArgumentException("Group path is too long.");
        if (string.IsNullOrWhiteSpace(value)) return [];
        var groups = value.TrimStart().StartsWith("[", StringComparison.Ordinal)
            ? ReadHierarchyJson(value)
            : value.Split(" / ", StringSplitOptions.TrimEntries).Select(part =>
        {
            int colon = part.IndexOf(':');
            if (colon < 1 || colon == part.Length - 1) throw new ArgumentException("Use Level: Value / Level: Value for group paths.");
            return new PlanGroupLevel(part[..colon].Trim(), part[(colon + 1)..].Trim());
        }).ToArray();
        Validate(new SubjectPlan(0, "Subjects", groups, "", false, []));
        return groups;
    }
    private static PlanGroupLevel[] ReadHierarchyJson(string value)
    {
        try { return JsonSerializer.Deserialize<PlanGroupLevel[]>(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new ArgumentException("Invalid hierarchy JSON."); }
        catch (JsonException e) { throw new ArgumentException("Invalid hierarchy JSON.", e); }
    }
    public static void Validate(SubjectPlan plan)
    {
        if (plan.SubjectKind is not ("Individual" or "Group" or "Item") || plan.Identification is { Count: > 20 } || plan.Identification?.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Key.Length > 100 || p.Value is null || p.Value.Length > 500) == true)
            throw new ArgumentException("Supply valid identification values of at most 500 characters.");
        if (plan.Revision < 0 || string.IsNullOrWhiteSpace(plan.Category) || plan.Category.Length > 100)
            throw new ArgumentException("Supply a category of at most 100 characters.");
        if (plan.Role is null || plan.Role.Length > 200 || plan.Groups is null || plan.Groups.Length > 8 || plan.Groups.Any(g => g is null || string.IsNullOrWhiteSpace(g.Name) || string.IsNullOrWhiteSpace(g.Value) || g.Name.Length > 100 || g.Value.Length > 100))
            throw new ArgumentException("Supply up to eight named hierarchy levels and a role of at most 200 characters.");
        if (plan.Groups.Select(g => g.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Groups.Length)
            throw new ArgumentException("Hierarchy level names must be unique within a subject path.");
        if (plan.Shots is null || plan.Shots.Length > 20 || plan.Shots.Any(s => s is null))
            throw new ArgumentException("Supply at most 20 required shot types.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var shot in plan.Shots)
        {
            if (string.IsNullOrWhiteSpace(shot.Name) || shot.Name.Length > 100 || !names.Add(shot.Name.Trim()))
                throw new ArgumentException("Required shot names must be nonempty and unique.");
            if (shot.Status is not ("Pending" or "Awaiting review" or "Approved" or "Needs retake") || shot.AssetIds is null || shot.AssetIds.Length > 100 || shot.AssetIds.Any(id => id == Guid.Empty) || shot.AssetIds.Distinct().Count() != shot.AssetIds.Length || shot.Note is null || shot.Note.Length > 2000 || shot.Instructions is null || shot.Instructions.Length > 2000)
                throw new ArgumentException("Correct the shot status, files or reviewer note.");
            if (shot.Status == "Approved" && (!plan.IdentityConfirmed || shot.ApprovedVersionId is null || shot.AssetIds.Length == 0))
                throw new ArgumentException("Confirm identity and select a photograph version before approving a required shot.");
            if (shot.Status != "Approved" && shot.ApprovedVersionId is not null)
                throw new ArgumentException("Only approved shots can retain an approved photograph version.");
        }
    }
}
