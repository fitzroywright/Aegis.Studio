using System.Text.Json;
using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Production;

public sealed class CommercialTemplateStore(StudioDatabase database)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static NpgsqlCommand Command(NpgsqlConnection c, NpgsqlTransaction? tx, string sql, params object?[] values)
    {
        var q = new NpgsqlCommand(sql, c, tx);
        foreach (var value in values) q.Parameters.AddWithValue(value ?? DBNull.Value);
        return q;
    }
    internal static int Execute(NpgsqlConnection c, NpgsqlTransaction? tx, string sql, params object?[] values)
    { using var q = Command(c, tx, sql, values); return q.ExecuteNonQuery(); }
    public IReadOnlyList<TemplateVersion> List(string kind, bool includeInactive = false)
    {
        using var c = database.OpenConnection();
        using var q = Command(c, null, "select id,kind,version,active,definition::text from studio_commercial_templates where kind=$1 and ($2 or active) order by definition->>'name' limit 500", kind, includeInactive);
        using var r = q.ExecuteReader(); var rows = new List<TemplateVersion>();
        while (r.Read()) rows.Add(Read(r)); return rows;
    }
    public TemplateVersion? Get(Guid id)
    { using var c = database.OpenConnection(); return Get(c, null, id); }
    internal static TemplateVersion? Get(NpgsqlConnection c, NpgsqlTransaction? tx, Guid id)
    {
        using var q = Command(c, tx, "select id,kind,version,active,definition::text from studio_commercial_templates where id=$1", id);
        using var r = q.ExecuteReader(); return r.Read() ? Read(r) : null;
    }
    private static TemplateVersion Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.GetBoolean(3), JsonSerializer.Deserialize<CommercialTemplate>(r.GetString(4), Json)!);
    public TemplateVersion Save(Guid? id, string kind, CommercialTemplate definition, int expectedVersion, bool active, string actor)
    {
        definition = JobGraphStore.Normalize(definition);
        CommercialRules.Validate(definition, kind);
        using var c = database.OpenConnection(); using var tx = c.BeginTransaction();
        Guid template = id ?? Guid.NewGuid(); int version = 1;
        Execute(c,tx,"select pg_advisory_xact_lock(hashtextextended($1,0))","workflow-template:"+template);
        if (id.HasValue)
        {
            using var q = Command(c, tx, "select version,kind from studio_commercial_templates where id=$1 for update", template);
            using var r = q.ExecuteReader();
            if (!r.Read()) throw new KeyNotFoundException("Template was not found.");
            if (r.GetInt32(0) != expectedVersion || r.GetString(1) != kind) throw new InvalidOperationException("Template changed. Refresh before saving.");
            version = expectedVersion + 1;
        }
        foreach(var step in definition.Steps.Where(s=>!string.IsNullOrWhiteSpace(s.DefaultAssignee)))
        {
            using var employee=Command(c,tx,"select exists(select 1 from studio_employees where lower(user_name)=lower($1) and is_active and is_assignable)",step.DefaultAssignee);
            if(!(bool)employee.ExecuteScalar()!)throw new ArgumentException("Choose an active, assignable default employee.");
        }
        foreach (var product in definition.Products ?? [])
        {
            using var q = Command(c, tx, "select exists(select 1 from studio_pos_catalog where id=$1 and is_active)", product.CatalogItemId);
            if (!(bool)q.ExecuteScalar()!) throw new ArgumentException("Select active catalog products.");
        }
        string json = JsonSerializer.Serialize(definition, Json);
        Execute(c, tx, """
            insert into studio_commercial_templates(id,kind,version,active,definition,updated_by) values($1,$2,$3,$4,$5::jsonb,$6)
            on conflict(id) do update set version=excluded.version,active=excluded.active,definition=excluded.definition,updated_by=excluded.updated_by,updated_at=now()
            """, template, kind, version, active, json, actor);
        Execute(c, tx, "insert into studio_commercial_template_versions(template_id,version,definition,created_by) values($1,$2,$3::jsonb,$4)", template, version, json, actor);
        tx.Commit(); return new(template, kind, version, active, definition);
    }
    public void ApplyToJob(Guid job, ApplyJobTemplateRequest request, string actor)
    {
        new JobGraphStore(database).Apply(job, request, actor);
    }
    internal static void ApplyNewJob(NpgsqlConnection c,NpgsqlTransaction tx,Guid job,TemplateVersion template,Guid operation,string actor)
    {
        if(!template.Active||template.Kind!="Job")throw new ArgumentException("Select an active Job Template.");
        Execute(c, tx, "insert into studio_job_template_instances(job_id,template_id,template_version,definition,operation_id,created_by) values($1,$2,$3,$4::jsonb,$5,$6)", job, template.Id, template.Version, JsonSerializer.Serialize(template.Definition, Json), operation, actor);
        JobGraphStore.Instantiate(c, tx, job, template.Definition, actor);
        foreach (var product in template.Definition.Products ?? [])
            Execute(c, tx, "insert into studio_job_catalog_agreements(id,job_id,catalog_item_id,quantity,specifications) values($1,$2,$3,$4,$5)", Guid.NewGuid(), job, product.CatalogItemId, product.Quantity, product.Specifications);
    }
    internal static void InstantiateSteps(NpgsqlConnection c, NpgsqlTransaction tx, Guid job, Guid? order, TemplateStep[] steps, string actor)
    {
        var ids = steps.ToDictionary(s => s.Key, _ => Guid.NewGuid());
        int position=0;
        foreach (var step in steps)
        {
            Execute(c, tx, """
                insert into studio_job_activities(id,job_id,activity_type,title,status,priority,assigned_to,location,created_by,created_at_utc,updated_at_utc,
                  work_order_id,template_step_key,production_area,step_definition,quality_control_required,optional_step,approved_image_required,step_order)
                values($1,$2,$3,$4,'NotStarted','Normal',$5,$6,$7,now(),now(),$8,$9,$10,$11::jsonb,$12,$13,$14,$15)
                """, ids[step.Key], job, step.ActivityType, step.Title, step.DefaultAssignee, step.Area, actor, order, step.Key, step.Area,
                JsonSerializer.Serialize(step, Json), step.QualityControl, step.Optional, step.RequiresApprovedImage,position++);
            foreach (var dependency in step.DependsOn ?? [])
                Execute(c, tx, "insert into studio_activity_dependencies(activity_id,depends_on_id) values($1,$2)", ids[step.Key], ids[dependency]);
        }
    }
    public static void GuardTask(NpgsqlConnection c, NpgsqlTransaction? tx, Guid activity, string status)
    {
        JobGraphStore.GuardTask(c, tx, activity, status);
        if (status is not ("InProgress" or "Completed" or "Cancelled")) return;
        using(var variation=Command(c,tx,"select exists(select 1 from studio_job_activities a join studio_catalog_work_orders w on w.id=a.work_order_id where a.id=$1 and nullif(a.step_definition->>'variation','') is not null and position(lower(a.step_definition->>'variation') in lower(coalesce(w.specifications,'')))=0)",activity))
            if((bool)variation.ExecuteScalar()! && status!="Cancelled")throw new ArgumentException("This variation does not apply to the ordered specifications.");
        using var q = Command(c, tx, """
            select a.optional_step,a.quality_control_required,a.approved_image_required,w.source_version_id,w.exception_reason,
              exists(select 1 from studio_activity_dependencies d join studio_job_activities p on p.id=d.depends_on_id where d.activity_id=a.id and p.status not in ('Completed','Cancelled')),
              exists(select 1 from studio_job_activities p where p.work_order_id=a.work_order_id and p.id<>a.id and not p.optional_step and not p.quality_control_required and p.status<>'Completed'),
              w.quantity,a.work_order_id
            from studio_job_activities a left join studio_catalog_work_orders w on w.id=a.work_order_id where a.id=$1
            """, activity);
        using var r = q.ExecuteReader(); if (!r.Read()) throw new KeyNotFoundException("Task was not found.");
        if (status == "Cancelled" && !r.IsDBNull(8) && !r.GetBoolean(0)) throw new ArgumentException("Only optional template steps may be skipped.");
        if (status == "Cancelled") return;
        if (r.GetBoolean(5)) throw new ArgumentException("Complete prerequisite steps first.");
        if (!r.IsDBNull(8))
        {
            if (!r.IsDBNull(4)) throw new ArgumentException("Resolve the work-order amendment/refund exception before continuing.");
            if (r.GetInt32(7) == 0) throw new ArgumentException("This work order is cancelled.");
            if (r.GetBoolean(2) && r.IsDBNull(3)) throw new ArgumentException("Link an approved Job Gallery image before beginning this step.");
            if (status == "Completed" && r.GetBoolean(1) && r.GetBoolean(6)) throw new ArgumentException("Complete mandatory production steps before final quality control.");
        }
    }
    public static void GuardPublication(NpgsqlConnection c, NpgsqlTransaction? tx, Guid job)
    {
        JobGraphStore.GuardProductionReady(c, tx, job);
        using var q = Command(c, tx, """
            select exists(select 1 from studio_catalog_work_orders w where w.job_id=$1 and
             (w.exception_reason is not null or (w.quantity>0 and
              (not exists(select 1 from studio_job_activities a where a.work_order_id=w.id and a.quality_control_required and a.status='Completed') or
               exists(select 1 from studio_job_activities a where a.work_order_id=w.id and not a.optional_step and a.status<>'Completed')))))
            """, job);
        if ((bool)q.ExecuteScalar()!) throw new ArgumentException("Complete all work orders and final quality control before delivery or Customer Gallery publication.");
    }
}
