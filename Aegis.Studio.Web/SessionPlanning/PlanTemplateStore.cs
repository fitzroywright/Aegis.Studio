using System.Text.Json;
using Aegis.Studio.Web.Persistence;
using Npgsql;
namespace Aegis.Studio.Web.SessionPlanning;

public sealed class PlanTemplateStore(StudioDatabase database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static NpgsqlCommand Command(NpgsqlConnection c, NpgsqlTransaction? tx, string sql, params object[] values)
    { var q = new NpgsqlCommand(sql, c, tx); foreach (var v in values) q.Parameters.AddWithValue(v); return q; }
    public IReadOnlyList<PlanTemplateVersion> List()
    {
        using var c = database.OpenConnection();
        using var q = Command(c, null, "select id,revision,active,definition::text from studio_session_plan_templates order by lower(definition->>'name'),id");
        using var r = q.ExecuteReader(); var result = new List<PlanTemplateVersion>();
        while (r.Read()) result.Add(new(r.GetGuid(0), r.GetInt64(1), r.GetBoolean(2), JsonSerializer.Deserialize<PlanTemplate>(r.GetString(3), Json)!));
        return result;
    }
    public PlanTemplateVersion Save(Guid id, SavePlanTemplate request, string actor)
    {
        PlanTemplateRules.Validate(request.Definition);
        using var c = database.OpenConnection(); using var tx = c.BeginTransaction();
        using (var guard = Command(c, tx, "select pg_advisory_xact_lock(hashtextextended($1,0))", "session-plan-template:" + id)) guard.ExecuteNonQuery();
        using (var check = Command(c, tx, "select coalesce((select revision from studio_session_plan_templates where id=$1),0)", id))
            if ((long)check.ExecuteScalar()! != request.Revision) throw new InvalidOperationException("Template changed. Reload before saving.");
        foreach (var workspace in request.Definition.WorkspaceIds)
        {
            using var check = Command(c, tx, "select exists(select 1 from studio_workspaces where id=$1)", workspace);
            if (!(bool)check.ExecuteScalar()!) throw new ArgumentException("A selected workspace no longer exists.");
        }
        long revision = checked(request.Revision + 1);
        using var write = Command(c, tx, """
            insert into studio_session_plan_templates(id,revision,active,definition,updated_by) values($1,$2,$3,$4::jsonb,$5)
            on conflict(id) do update set revision=excluded.revision,active=excluded.active,definition=excluded.definition,updated_by=excluded.updated_by,updated_at_utc=now()
            """, id, revision, request.Active, JsonSerializer.Serialize(request.Definition, Json), actor);
        write.ExecuteNonQuery(); tx.Commit(); return new(id, revision, request.Active, request.Definition);
    }
    internal static JobPlanStructure? ReadJob(NpgsqlConnection c, NpgsqlTransaction? tx, Guid job)
    {
        using var q = Command(c, tx, "select revision,template_id,template_revision,definition::text from studio_job_plan_structures where job_id=$1", job);
        using var r = q.ExecuteReader();
        return r.Read() ? new(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetGuid(1), r.IsDBNull(2) ? null : r.GetInt64(2), JsonSerializer.Deserialize<PlanTemplate>(r.GetString(3), Json)!) : null;
    }
    public JobPlanStructure? GetJob(Guid job) { using var c = database.OpenConnection(); return ReadJob(c, null, job); }
    public IReadOnlyList<PlanTemplateVersion> Available(Guid job)
    {
        using var c = database.OpenConnection(); using var q = Command(c, null, "select workspace_id from studio_jobs where id=$1", job);
        Guid workspace = (Guid)(q.ExecuteScalar() ?? throw new KeyNotFoundException());
        return List().Where(t => t.Active && (t.Definition.WorkspaceIds.Length == 0 || t.Definition.WorkspaceIds.Contains(workspace))).ToArray();
    }
    public JobPlanStructure Apply(Guid job, ApplyPlanTemplate request, string actor)
    {
        using var c = database.OpenConnection(); using var tx = c.BeginTransaction();
        // Serialize with roster creation: the job copy must precede or follow a complete roster save.
        using var guard = Command(c, tx, "select workspace_id from studio_jobs where id=$1 for update", job);
        Guid workspace = (Guid)(guard.ExecuteScalar() ?? throw new KeyNotFoundException());
        var previous = ReadJob(c, tx, job);
        if ((previous?.Revision ?? 0) != request.Revision) throw new InvalidOperationException("Job structure changed. Reload before applying.");
        PlanTemplate definition;
        if (request.TemplateId is Guid template)
        {
            using var q = Command(c, tx, "select revision,active,definition::text from studio_session_plan_templates where id=$1 for share", template);
            using var r = q.ExecuteReader();
            if (!r.Read()) throw new KeyNotFoundException();
            if (r.GetInt64(0) != request.TemplateRevision) throw new InvalidOperationException("Template changed. Reload its preview before applying.");
            if (!r.GetBoolean(1)) throw new ArgumentException("Choose an active template.");
            definition = JsonSerializer.Deserialize<PlanTemplate>(r.GetString(2), Json)!;
            if (definition.WorkspaceIds.Length > 0 && !definition.WorkspaceIds.Contains(workspace)) throw new ArgumentException("This template is unavailable in the job's workspace.");
        }
        else definition = request.Definition ?? new("Blank plan", "", [], []);
        PlanTemplateRules.Validate(definition);
        // A snapshot never rewrites existing identities, requirements or approvals.
        long revision = checked(request.Revision + 1);
        using var write = Command(c, tx, """
            insert into studio_job_plan_structures(job_id,revision,template_id,template_revision,definition,updated_by) values($1,$2,$3,$4,$5::jsonb,$6)
            on conflict(job_id) do update set revision=excluded.revision,template_id=excluded.template_id,template_revision=excluded.template_revision,definition=excluded.definition,updated_by=excluded.updated_by,updated_at_utc=now()
            """, job, revision, (object?)request.TemplateId ?? DBNull.Value, request.TemplateId.HasValue ? (object)request.TemplateRevision : DBNull.Value, JsonSerializer.Serialize(definition, Json), actor);
        write.ExecuteNonQuery();
        using var audit = Command(c, tx, "insert into studio_operational_audit(id,job_id,action,user_name,detail,created_at_utc) values($1,$2,'SessionPlanStructureApplied',$3,$4,now())", Guid.NewGuid(), job, actor, $"Copied {definition.Name}; structure revision {revision}. Existing subjects retained.");
        audit.ExecuteNonQuery(); tx.Commit(); return new(revision, request.TemplateId, request.TemplateId.HasValue ? request.TemplateRevision : null, definition);
    }
    internal static void InitializeSubject(NpgsqlConnection c, NpgsqlTransaction tx, Guid job, JsonElement row, JobPlanStructure? structure)
    {
        if (structure is null || structure.Definition.Categories.Length == 0) return;
        string categoryName = row.TryGetProperty("planCategory", out var category) ? category.GetString() ?? "" : "";
        var selected = structure.Definition.Categories.FirstOrDefault(x => string.Equals(x.Name, categoryName, StringComparison.OrdinalIgnoreCase));
        if (selected is null && string.IsNullOrWhiteSpace(categoryName) && structure.Definition.Categories.Length == 1) selected = structure.Definition.Categories[0];
        if (selected is null) throw new ArgumentException("Choose a category from the job's copied structure for each new subject.");
        PlanGroupLevel[] groups = row.TryGetProperty("planGroups", out var path) ? SubjectPlanRules.ParseHierarchy(path.GetString() ?? "") : [];
        if (groups.Length > 0 && !groups.Select(g => g.Name).SequenceEqual(selected.GroupingLevels, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Group paths must match the selected category's ordered grouping levels.");
        var plan = PlanTemplateRules.NewSubject(selected, groups) with { Revision = 1 };
        using var write = Command(c, tx, "update studio_job_subjects set attributes=jsonb_set(coalesce(attributes,'{}'::jsonb),'{sessionPlan}',$3::jsonb,true),updated_at_utc=clock_timestamp() where job_id=$1 and lower(reference)=lower($2) and not is_system", job, row.GetProperty("reference").GetString()!, JsonSerializer.Serialize(plan, Json));
        write.ExecuteNonQuery();
    }
}
