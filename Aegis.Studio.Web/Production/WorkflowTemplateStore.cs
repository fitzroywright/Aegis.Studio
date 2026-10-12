using System.Text.Json;
using Aegis.Studio.Web.Persistence;
using static Aegis.Studio.Web.Production.CommercialTemplateStore;

namespace Aegis.Studio.Web.Production;

public sealed record WorkflowDraft(Guid TemplateId, int Revision, int BaseVersion, bool Active, CommercialTemplate Definition);
public sealed record SaveWorkflowDraft(CommercialTemplate Definition, int ExpectedRevision = 0, int BaseVersion = 0, bool Active = true);
public sealed record PublishWorkflowDraft(int ExpectedRevision);
public sealed class WorkflowTemplateStore(StudioDatabase database)
{
    public WorkflowDraft[] Drafts()
    {
        using var c = database.OpenConnection(); using var q = Command(c, null, "select template_id,revision,base_version,active,definition::text from studio_workflow_template_drafts order by definition->>'name'");
        using var r = q.ExecuteReader(); var result = new List<WorkflowDraft>();
        while (r.Read()) result.Add(new(r.GetGuid(0), r.GetInt32(1), r.GetInt32(2), r.GetBoolean(3), JsonSerializer.Deserialize<CommercialTemplate>(r.GetString(4), Json)!)); return result.ToArray();
    }
    public WorkflowDraft SaveDraft(Guid id, SaveWorkflowDraft request, string actor)
    {
        if(id==Guid.Empty)throw new ArgumentException("Template identity is required.");
        // A draft can be incomplete. Publication is the validation boundary.
        if (string.IsNullOrWhiteSpace(request.Definition.Name) || request.Definition.Name.Length > 200) throw new ArgumentException("Provide a template name of at most 200 characters.");
        if (JsonSerializer.Serialize(request.Definition, Json).Length > 500000) throw new ArgumentException("Template is too large.");
        using var c = database.OpenConnection(); using var tx = c.BeginTransaction();
        Execute(c, tx, "select pg_advisory_xact_lock(hashtextextended($1,0))", "workflow-template:" + id);
        using (var current = Command(c, tx, "select revision from studio_workflow_template_drafts where template_id=$1", id))
        { var prior = current.ExecuteScalar(); if ((prior is int revision ? revision : 0) != request.ExpectedRevision) throw new InvalidOperationException("Draft changed. Refresh before saving."); }
        var published = Get(c, tx, id);
        int version = published?.Version ?? 0;
        if (published is not null && published.Kind != "Job") throw new ArgumentException("Only Job Templates have workflow drafts.");
        if (version != request.BaseVersion) throw new InvalidOperationException("Published template changed. Refresh before editing.");
        int next = request.ExpectedRevision + 1;
        Execute(c, tx, """
            insert into studio_workflow_template_drafts(template_id,revision,base_version,active,definition,updated_by)
            values($1,$2,$3,$4,$5::jsonb,$6) on conflict(template_id) do update set revision=excluded.revision,
              definition=excluded.definition,active=excluded.active,updated_by=excluded.updated_by,updated_at=now()
            """, id, next, version, request.Active, JsonSerializer.Serialize(request.Definition, Json), actor);
        tx.Commit(); return new(id, next, version, request.Active, request.Definition);
    }
    public TemplateVersion Publish(Guid id, int expectedRevision, string actor)
    {
        using var c = database.OpenConnection(); using var tx = c.BeginTransaction();
        Execute(c, tx, "select pg_advisory_xact_lock(hashtextextended($1,0))", "workflow-template:" + id);
        CommercialTemplate definition; bool active; int baseVersion;
        using (var q = Command(c, tx, "select definition::text,revision,active,base_version from studio_workflow_template_drafts where template_id=$1 for update", id))
        { using var r = q.ExecuteReader(); if (!r.Read()) throw new KeyNotFoundException("Save a draft first."); if (r.GetInt32(1) != expectedRevision) throw new InvalidOperationException("Draft changed. Refresh before publishing."); definition = JsonSerializer.Deserialize<CommercialTemplate>(r.GetString(0), Json)!; active = r.GetBoolean(2); baseVersion = r.GetInt32(3); }
        definition = JobGraphStore.Normalize(definition); CommercialRules.Validate(definition, "Job");
        var current = Get(c, tx, id); if ((current?.Version ?? 0) != baseVersion) throw new InvalidOperationException("Published version changed. Refresh the draft.");
        foreach (var task in definition.Steps.Where(s => !string.IsNullOrWhiteSpace(s.DefaultAssignee)))
        { using var q = Command(c, tx, "select exists(select 1 from studio_employees where lower(user_name)=lower($1) and is_active and is_assignable)", task.DefaultAssignee); if (!(bool)q.ExecuteScalar()!) throw new ArgumentException($"{task.Key}: select an active assignable employee."); }
        var graph = JobGraphStore.Graph(definition);
        IEnumerable<Guid> ConditionIds(WorkflowCondition? condition) => condition is null ? [] : (condition.CatalogItemId.HasValue ? new[] { condition.CatalogItemId.Value } : []).Concat((condition.Conditions ?? []).SelectMany(ConditionIds));
        foreach (var product in (definition.Products ?? []).Select(p => p.CatalogItemId).Concat(WorkflowGraphEngine.Flatten(graph).Edges.SelectMany(e => ConditionIds(e.Condition))).Distinct())
        { using var q = Command(c, tx, "select exists(select 1 from studio_pos_catalog where id=$1 and is_active)", product); if (!(bool)q.ExecuteScalar()!) throw new ArgumentException($"{product}: select an active catalog service."); }
        int version = baseVersion + 1; string json = JsonSerializer.Serialize(definition, Json);
        Execute(c, tx, """
            insert into studio_commercial_templates(id,kind,version,active,definition,updated_by) values($1,'Job',$2,$3,$4::jsonb,$5)
            on conflict(id) do update set version=excluded.version,active=excluded.active,definition=excluded.definition,updated_by=excluded.updated_by,updated_at=now()
            """, id, version, active, json, actor);
        Execute(c, tx, "insert into studio_commercial_template_versions(template_id,version,definition,created_by) values($1,$2,$3::jsonb,$4)", id, version, json, actor);
        Execute(c, tx, "delete from studio_workflow_template_drafts where template_id=$1", id);
        tx.Commit(); return new(id, "Job", version, active, definition);
    }
    public object[] Versions(Guid id)
    {
        using var c = database.OpenConnection(); using var q = Command(c, null, "select version,definition::text,created_by,created_at from studio_commercial_template_versions where template_id=$1 order by version desc", id); using var r = q.ExecuteReader(); var result = new List<object>();
        while (r.Read()) result.Add(new { version = r.GetInt32(0), definition = JsonSerializer.Deserialize<CommercialTemplate>(r.GetString(1), Json), createdBy = r.GetString(2), createdAt = r.GetFieldValue<DateTimeOffset>(3) }); return result.ToArray();
    }
}
