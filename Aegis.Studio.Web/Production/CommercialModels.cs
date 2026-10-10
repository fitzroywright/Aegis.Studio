namespace Aegis.Studio.Web.Production;

public sealed record TemplateStep(string Key, string Title, string ActivityType = "Other", string Area = "Editing",
    string[]? DependsOn = null, string[]? Skills = null, int EstimatedMinutes = 0, string? Materials = null,
    decimal EstimatedCost = 0, bool QualityControl = false, bool Optional = false, string? Variation = null,
    string? DefaultAssignee = null, bool RequiresApprovedImage = false);
public sealed record TemplateProduct(Guid CatalogItemId, int Quantity = 1, string? Specifications = null);
public sealed record CommercialTemplate(string Name, string Description, TemplateStep[] Steps,
    TemplateProduct[]? Products = null, string[]? RequiredRoles = null, string[]? Skills = null,
    bool SchedulingRequired = false, bool InHouse = true);
public sealed record TemplateVersion(Guid Id, string Kind, int Version, bool Active, CommercialTemplate Definition);
public sealed record BundleComponent(Guid CatalogItemId, int Quantity);
public sealed record WorkOrderCommand(string Status, string? AssignedTo, string? BlockedReason, Guid OperationId, long ExpectedRevision);
public sealed record ApplyJobTemplateRequest(Guid TemplateId, Guid OperationId, long ExpectedRevision);

public static class CommercialRules
{
    public static readonly string[] Areas = ["Editing", "Printing", "Framing", "Album Production", "Assembly", "Quality Control"];
    public static void Validate(CommercialTemplate template, string kind)
    {
        if (kind is not ("Job" or "WorkOrder")) throw new ArgumentException("Unknown template kind.");
        if (string.IsNullOrWhiteSpace(template.Name) || template.Name.Length > 200 || template.Description.Length > 4000)
            throw new ArgumentException("Provide a template name and a description of at most 4,000 characters.");
        if (template.Steps is null || template.Steps.Length is < 1 or > 100) throw new ArgumentException("Provide 1–100 steps.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in template.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.Key) || step.Key.Length > 100 || !keys.Add(step.Key) ||
                string.IsNullOrWhiteSpace(step.Title) || step.Title.Length > 200 || !Areas.Contains(step.Area) ||
                step.EstimatedMinutes < 0 || step.EstimatedCost < 0)
                throw new ArgumentException("Steps need unique keys, titles, valid production areas and nonnegative estimates.");
        }
        // Dependencies must point backwards in the declared execution order; this also rejects cycles.
        keys.Clear();
        foreach (var step in template.Steps)
        {
            if ((step.DependsOn ?? []).Any(key => !keys.Contains(key)))
                throw new ArgumentException("Dependencies must refer to earlier steps.");
            keys.Add(step.Key);
        }
        if (kind == "WorkOrder" && !template.Steps.Any(s => s.QualityControl && !s.Optional))
            throw new ArgumentException("A Work Order Template requires a mandatory final quality-control step.");
        if (kind == "WorkOrder" && !template.Steps.Last(s => !s.Optional).QualityControl)
            throw new ArgumentException("The last mandatory step must be quality control.");
        if ((template.Products ?? []).Any(p => p.CatalogItemId == Guid.Empty || p.Quantity is < 1 or > 10000))
            throw new ArgumentException("Select catalog items and positive quantities.");
    }
    public static decimal Price(decimal standard, decimal? requested, decimal discountPercent, bool authorized)
    {
        if (standard < 0 || requested < 0 || discountPercent is < 0 or > 100) throw new ArgumentException("Invalid price or discount.");
        if (!authorized && ((requested.HasValue && requested.Value != standard) || discountPercent != 0))
            throw new UnauthorizedAccessException("Price overrides and discounts require authorization.");
        return decimal.Round((requested ?? standard) * (1 - discountPercent / 100m), 2, MidpointRounding.AwayFromZero);
    }
    public static bool Ready(IEnumerable<string> dependencies) => dependencies.All(s => s is "Completed" or "Cancelled");
}
