using Aegis.Studio.Web.SessionPlanning;
using Npgsql;
using Xunit;
namespace Aegis.Studio.Web.Tests;

public sealed class PlanTemplateTests
{
    private static PlanTemplate Definition(Guid[]? workspaces = null) => new("Campaign", "Configurable", workspaces ?? [],
        [new("Performers", "Individual", ["Region", "Cast"], [new("Permit", true)], [new("Programme portrait", true, "Neutral backdrop"), new("Creative", false, "Optional pose")])]);
    [Fact]
    public void GenericStructureHasNoSchoolSpecificSchemaAndRetainsOptionalRequirements()
    {
        var definition = Definition(); PlanTemplateRules.Validate(definition);
        var plan = PlanTemplateRules.NewSubject(definition.Categories[0], [new("Region", "East"), new("Cast", "A")]);
        Assert.Equal("Performers", plan.Category); Assert.False(plan.Shots[1].Required);
        Assert.Equal("Neutral backdrop", plan.Shots[0].Instructions); Assert.True(plan.IdentificationFields![0].Required);
        Assert.All(plan.Shots, p => { Assert.Equal("Pending", p.Status); Assert.Empty(p.AssetIds); Assert.Null(p.ApprovedVersionId); });
        PlanTemplateRules.Validate(new("Blank", "", [], []));
    }
    [Fact]
    public void DuplicateNamesAndMalformedStructuresAreRejected()
    {
        var d = Definition();
        Assert.Throws<ArgumentException>(() => PlanTemplateRules.Validate(d with { Categories = [d.Categories[0], d.Categories[0] with { Name = " performers " }] }));
        Assert.Throws<ArgumentException>(() => PlanTemplateRules.Validate(d with { Categories = [d.Categories[0] with { GroupingLevels = ["Region", "region"] }] }));
        Assert.Throws<ArgumentException>(() => PlanTemplateRules.Validate(d with { Categories = [d.Categories[0] with { SubjectKind = "Student" }] }));
    }
    [StudioPostgresFact]
    public void CopiedRequirementsInitializeNewRosterSubjectsAndIdentityFieldsAreEnforced()
    {
        using var context = new IntegratedWorkflowTests.WorkflowContext();
        var templates = new PlanTemplateStore(context.Database);
        var template = templates.Save(Guid.NewGuid(), new(0, true, Definition()), "Planner");
        templates.Apply(context.Job, new(template.Id, template.Revision, 0), "Planner");
        SessionPlanTests.Initialize(context);
        var roster = new Aegis.Studio.Web.Intake.IntakeRosterStore(context.Database);
        var current = roster.ReadJob(context.Job);
        var rows = System.Text.Json.JsonSerializer.SerializeToElement(new[] { new { name = "Same name", reference = "CAST-1", collection = "Cast A", planCategory = "Performers", planGroups = "Region: East / Cast: A", pickupNotification = "None" } });
        roster.CommitJob(context.Job, new(current.Revision, rows), "Planner", true);
        using var q = new NpgsqlCommand("select id,attributes->'sessionPlan' from studio_job_subjects where job_id=$1 and reference='CAST-1'", context.Connection);
        q.Parameters.AddWithValue(context.Job);
        Guid subject; SubjectPlan saved;
        using (var reader = q.ExecuteReader())
        {
            Assert.True(reader.Read()); subject = reader.GetGuid(0);
            saved = System.Text.Json.JsonSerializer.Deserialize<SubjectPlan>(reader.GetString(1), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        }
        Assert.Equal(2, saved.Shots.Length); Assert.False(saved.Shots[1].Required);
        var subjects = new SubjectPlanStore(context.Database);
        Assert.Throws<ArgumentException>(() => subjects.Save(context.Job, subject, saved with { IdentityConfirmed = true, IdentificationFields = [] }, "Planner", false));
        var identified = subjects.Save(context.Job, subject, saved with { IdentityConfirmed = true, Identification = new() { ["Permit"] = "P-123" } }, "Planner", false);
        Assert.Equal("P-123", identified.Identification!["Permit"]);
        templates.Apply(context.Job, new(null, 0, 1, new("Blank", "", [], [])), "Planner");
        Assert.Equal("CAST-1", roster.ReadJob(context.Job).Rows.EnumerateArray().Single().GetProperty("reference").GetString());
    }
    [StudioPostgresFact]
    public void JobCopySurvivesTemplateChangesAndEnforcesScopeAndRevisions()
    {
        using var context = new IntegratedWorkflowTests.WorkflowContext();
        var store = new PlanTemplateStore(context.Database); Guid id = Guid.NewGuid();
        var first = store.Save(id, new(0, true, Definition([context.Workspace])), "Planner");
        Assert.Single(store.Available(context.Job));
        var applied = store.Apply(context.Job, new(id, first.Revision, 0), "Planner");
        var changed = store.Save(id, new(first.Revision, true, Definition() with { Name = "Changed", Categories = [] }), "Planner");
        Assert.Equal("Campaign", store.GetJob(context.Job)!.Definition.Name); Assert.Single(store.GetJob(context.Job)!.Definition.Categories);
        Assert.Throws<InvalidOperationException>(() => store.Save(id, new(first.Revision, true, Definition()), "Planner"));
        Assert.Throws<InvalidOperationException>(() => store.Apply(context.Job, new(id, first.Revision, applied.Revision), "Planner"));
        Assert.Throws<InvalidOperationException>(() => store.Apply(context.Job, new(id, changed.Revision, 0), "Planner"));
        var other = Guid.NewGuid();
        using (var q = new NpgsqlCommand("insert into studio_workspaces(id,name,created_at_utc,updated_at_utc) values($1,'Other',now(),now())", context.Connection)) { q.Parameters.AddWithValue(other); q.ExecuteNonQuery(); }
        var scoped = store.Save(id, new(changed.Revision, true, Definition([other])), "Planner");
        Assert.Empty(store.Available(context.Job));
        Assert.Throws<ArgumentException>(() => store.Apply(context.Job, new(id, scoped.Revision, applied.Revision), "Planner"));
        var inactive = store.Save(id, new(scoped.Revision, false, Definition()), "Planner");
        Assert.Throws<ArgumentException>(() => store.Apply(context.Job, new(id, inactive.Revision, applied.Revision), "Planner"));
    }
}
