namespace Aegis.Studio.Web.SessionPlanning;

public static class PlanTemplateRules
{
    public static void Validate(PlanTemplate definition)
    {
        if (definition is null || !Text(definition.Name, 120) || definition.Description is null || definition.Description.Length > 2000)
            throw new ArgumentException("Supply a template name and description of at most 120 and 2000 characters.");
        if (definition.WorkspaceIds is null || definition.WorkspaceIds.Length > 100 || definition.WorkspaceIds.Any(id => id == Guid.Empty) || definition.WorkspaceIds.Distinct().Count() != definition.WorkspaceIds.Length)
            throw new ArgumentException("Choose valid, distinct workspaces; an empty selection means all workspaces.");
        if (definition.Categories is null || definition.Categories.Length > 30 || definition.Categories.Any(c => c is null)) throw new ArgumentException("Supply up to 30 categories.");
        Unique(definition.Categories.Select(c => c.Name));
        foreach (var category in definition.Categories)
        {
            if (category.SubjectKind is not ("Individual" or "Group" or "Item")) throw new ArgumentException("Choose Individual, Group or Item as the subject kind.");
            if (category.GroupingLevels is null || category.GroupingLevels.Length > 8) throw new ArgumentException("Supply up to eight grouping levels.");
            Unique(category.GroupingLevels);
            if (category.IdentificationFields is null || category.IdentificationFields.Length > 20 || category.IdentificationFields.Any(f => f is null)) throw new ArgumentException("Supply up to 20 identification fields.");
            Unique(category.IdentificationFields.Select(f => f.Name));
            if (category.PhotoRequirements is null || category.PhotoRequirements.Length > 20 || category.PhotoRequirements.Any(p => p is null || p.Instructions is null || p.Instructions.Length > 2000)) throw new ArgumentException("Supply up to 20 photo requirements with instructions of at most 2000 characters.");
            Unique(category.PhotoRequirements.Select(p => p.Name));
        }
    }
    private static bool Text(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max;
    private static void Unique(IEnumerable<string> values)
    {
        var names = values.ToArray();
        if (names.Any(n => !Text(n, 100)) || names.Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
            throw new ArgumentException("Names must be nonempty, unique within their section, and at most 100 characters.");
    }
    public static SubjectPlan NewSubject(PlanCategory category, PlanGroupLevel[] groups) => new(0, category.Name, groups, "", false,
        category.PhotoRequirements.Select(p => new PlannedShot(p.Name, "Pending", [], Required: p.Required, Instructions: p.Instructions)).ToArray(), new(), category.SubjectKind, category.IdentificationFields);
}
