using System.Text.Json;
using Aegis.Studio.Web.Persistence;
using Npgsql;
using static Aegis.Studio.Web.Production.CommercialTemplateStore;

namespace Aegis.Studio.Web.Production;

public sealed record GraphTask(Guid Id, string NodeId, string Title, string Status, string? AssignedTo, string? Role,
    string Eligibility, string[] Dependencies, bool Optional, string? Reason, string[]? DependencyLabels = null, string? ActivityType = null);
public sealed record JobGraphView(Guid JobId, Guid TemplateId, int TemplateVersion, long Revision,
    WorkflowGraph Graph, GraphEvaluation Evaluation, GraphTask[] Tasks, object[] History);
public sealed record TemplateImpact(string[] Remain, string[] Added, string[] Removed, string[] CompletedAffected,
    string[] AssignmentChanges, string[] DependencyChanges, bool RequiresConfirmation, string FinancialImplications, int TemplateVersion, Dictionary<string,string> TaskLabels);
public sealed record WorkflowServicesRequest(Guid[] CatalogItemIds, Guid OperationId, long ExpectedRevision, bool ConfirmChanges = false, string? Reason = null);
public sealed record GraphTaskCommand(string NodeId, string Action, Guid OperationId, long ExpectedRevision, string? Reason = null);

public sealed class JobGraphStore(StudioDatabase database)
{
    public static CommercialTemplate Normalize(CommercialTemplate template)
    {
        if (template.Workflow is null) return template;
        var issues = WorkflowGraphEngine.Validate(template.Workflow);
        if (issues.Length > 0) throw new ArgumentException(string.Join("\n", issues.Select(i => $"{i.NodeId}: {i.Message}")));
        return template with { Steps = WorkflowGraphEngine.Flatten(template.Workflow).Nodes.Where(n => n.Kind == "Task").Select(n => n.Task! with { DependsOn = [] }).ToArray() };
    }
    internal static WorkflowGraph Graph(CommercialTemplate template) => template.Workflow ?? WorkflowGraphEngine.FromSteps(template.Steps);
    internal static HashSet<Guid> Services(NpgsqlConnection c, NpgsqlTransaction? tx, Guid job)
    {
        using var q = Command(c, tx, """
            with selected as (
              select catalog_item_id from studio_job_catalog_agreements where job_id=$1 and workflow_selected
              union select catalog_item_id from studio_catalog_line_snapshots s
              join studio_pos_sale_lines l on l.id=s.sale_line_id join studio_pos_sales sale on sale.id=l.sale_id
              where sale.reference_type='StudioJob' and sale.reference_id=$1::text and sale.status=2
                and l.quantity>coalesce((select sum(r.quantity) from studio_pos_refund_lines r where r.sale_line_id=l.id),0)
            ) select catalog_item_id from selected
              union select (component->>'catalogItemId')::uuid from selected s join studio_pos_catalog p on p.id=s.catalog_item_id
              cross join lateral jsonb_array_elements(p.bundle) component
            """, job);
        using var r = q.ExecuteReader(); var result = new HashSet<Guid>(); while (r.Read()) result.Add(r.GetGuid(0)); return result;
    }
    internal static CommercialTemplate? Definition(NpgsqlConnection c, NpgsqlTransaction? tx, Guid job)
    {
        using var q = Command(c, tx, "select definition::text from studio_job_template_instances where job_id=$1", job);
        return q.ExecuteScalar() is string json ? JsonSerializer.Deserialize<CommercialTemplate>(json, Json) : null;
    }
    private static TemplateStep[] DefaultSteps() => [
        new("inquiry-booking", "Inquiry / Booking", "Booking", "Editing"),
        new("shoot", "Shoot", "Photography", "Editing", ["inquiry-booking"]),
        new("production", "Production", "Production", "Editing", ["shoot"]),
        new("delivery", "Delivery", "Delivery", "Assembly", ["production"]),
        new("completed", "Completed", "Archive", "Assembly", ["delivery"])
    ];
    private static void EnsureDefaultActivities(NpgsqlConnection c, NpgsqlTransaction tx, Guid job, string actor)
    {
        int position = 0;
        foreach (var step in DefaultSteps())
            Execute(c, tx, """
                insert into studio_job_activities(id,job_id,activity_type,title,status,priority,created_by,created_at_utc,updated_at_utc,
                  template_step_key,production_area,step_definition,quality_control_required,optional_step,approved_image_required,step_order,workflow_node_id)
                values($1,$2,$3,$4,'NotStarted','Normal',$5,now(),now(),$6,$7,$8::jsonb,false,false,false,$9,$6)
                on conflict(job_id,workflow_node_id) where workflow_node_id is not null do nothing
                """, Guid.NewGuid(), job, step.ActivityType, step.Title, actor, step.Key, step.Area, JsonSerializer.Serialize(step, Json), position++);
    }
    internal static Dictionary<string, string> Statuses(NpgsqlConnection c, NpgsqlTransaction? tx, Guid job)
    {
        using var q = Command(c, tx, "select workflow_node_id,status from studio_job_activities where job_id=$1 and workflow_node_id is not null", job);
        using var r = q.ExecuteReader(); var result = new Dictionary<string, string>(); while (r.Read()) result[r.GetString(0)] = r.GetString(1); return result;
    }
    internal static void Instantiate(NpgsqlConnection c, NpgsqlTransaction tx, Guid job, CommercialTemplate template, string actor)
    {
        var nodes = WorkflowGraphEngine.Flatten(Graph(template)).Nodes.Where(n => n.Kind == "Task").ToArray();
        int position = 0;
        foreach (var n in nodes)
        {
            var step = n.Task!;
            Execute(c, tx, """
                insert into studio_job_activities(id,job_id,activity_type,title,status,priority,assigned_to,location,created_by,created_at_utc,updated_at_utc,
                  template_step_key,production_area,step_definition,quality_control_required,optional_step,approved_image_required,step_order,workflow_node_id)
                values($1,$2,$3,$4,'NotStarted','Normal',$5,$6,$7,now(),now(),$8,$9,$10::jsonb,$11,$12,$13,$14,$8)
                on conflict(job_id,workflow_node_id) where workflow_node_id is not null do update set
                  title=case when studio_job_activities.status='Completed' then studio_job_activities.title else excluded.title end,
                  activity_type=case when studio_job_activities.status='Completed' then studio_job_activities.activity_type else excluded.activity_type end,
                  production_area=case when studio_job_activities.status='Completed' then studio_job_activities.production_area else excluded.production_area end,
                  assigned_to=coalesce(studio_job_activities.assigned_to,excluded.assigned_to),step_definition=excluded.step_definition,
                  optional_step=excluded.optional_step,quality_control_required=excluded.quality_control_required
                """, Guid.NewGuid(), job, step.ActivityType, step.Title, step.DefaultAssignee, step.Area, actor, n.Id, step.Area,
                JsonSerializer.Serialize(step, Json), step.QualityControl, step.Optional, step.RequiresApprovedImage, position++);
        }
    }
    public JobGraphView? Get(Guid job)
    {
        using var c = database.OpenConnection(); using var tx = c.BeginTransaction(System.Data.IsolationLevel.RepeatableRead);
        var definition = Definition(c, tx, job);
        Guid template; int version; long revision;
        if (definition is null)
        {
            using var q = Command(c, tx, "select workflow_revision from studio_jobs where id=$1", job);
            using var r = q.ExecuteReader(); if (!r.Read()) return null;
            revision = r.GetInt64(0); template = Guid.Empty; version = 1;
            var steps = DefaultSteps();
            var graph = WorkflowGraphEngine.FromSteps(steps);
            var states = Statuses(c, tx, job);
            var activityRows = new Dictionary<string, (Guid Id, string? AssignedTo)>();
            using (var activities = Command(c, tx, "select workflow_node_id,id,assigned_to from studio_job_activities where job_id=$1 and workflow_node_id is not null", job))
            using (var reader = activities.ExecuteReader())
                while (reader.Read()) activityRows[reader.GetString(0)] = (reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetString(2));
            foreach (var step in steps) if (!states.ContainsKey(step.Key)) states[step.Key] = "NotStarted";
            var evaluation = WorkflowGraphEngine.Evaluate(graph, states, new HashSet<Guid>());
            var flat = WorkflowGraphEngine.Flatten(graph).Nodes.ToDictionary(n => n.Id);
            var evaluationStates = evaluation.Nodes.ToDictionary(n => n.Id);
            var tasks = steps.Select(step =>
            {
                var state = evaluationStates[step.Key]; var activity = activityRows.GetValueOrDefault(step.Key);
                return new GraphTask(activity.Id, step.Key, step.Title, states[step.Key], activity.AssignedTo, null, state.Status,
                    state.Dependencies, false, state.Reason, state.Dependencies.Select(id => flat[id].Label).ToArray(), step.ActivityType);
            }).ToArray();
            tx.Commit();
            return new(job, template, version, revision, graph, evaluation, tasks, []);
        }
        using (var q = Command(c, tx, "select i.template_id,i.template_version,j.workflow_revision from studio_job_template_instances i join studio_jobs j on j.id=i.job_id where i.job_id=$1", job))
        { using var r = q.ExecuteReader(); r.Read(); template = r.GetGuid(0); version = r.GetInt32(1); revision = r.GetInt64(2); }
        var customGraph = Graph(definition); var customEvaluation = WorkflowGraphEngine.Evaluate(customGraph, Statuses(c, tx, job), Services(c, tx, job));
        var nodes = WorkflowGraphEngine.Flatten(customGraph).Nodes.ToDictionary(n => n.Id); var statesById = customEvaluation.Nodes.ToDictionary(n => n.Id);
        var customTasks = new List<GraphTask>();
        using (var q = Command(c, tx, "select id,workflow_node_id,title,status,assigned_to,optional_step,blocked_reason,activity_type from studio_job_activities where job_id=$1 and workflow_node_id is not null order by step_order", job))
        { using var r = q.ExecuteReader(); while (r.Read()) { var id = r.GetString(1); var state = statesById[id]; customTasks.Add(new(r.GetGuid(0), id, r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), nodes[id].Role, state.Status, state.Dependencies, r.GetBoolean(5), r.IsDBNull(6) ? state.Reason : r.GetString(6), state.Dependencies.Select(p=>nodes[p].Label).ToArray(), r.GetString(7))); } }
        var history = new List<object>();
        using (var q = Command(c, tx, "select title,status,assigned_to,completed_at_utc from studio_job_activities where job_id=$1 and workflow_node_id is null and template_step_key is not null and work_order_id is null order by created_at_utc", job))
        { using var r = q.ExecuteReader(); while (r.Read()) history.Add(new { title = r.GetString(0), status = r.GetString(1), assignedTo = r.IsDBNull(2) ? null : r.GetString(2), completedAtUtc = r.IsDBNull(3) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(3) }); }
        var taskMap=customTasks.ToDictionary(t=>t.NodeId);
        WorkflowGraph Display(WorkflowGraph flow,string prefix)=>flow with{Nodes=flow.Nodes.Select(n=>
            n.Kind=="Subflow"&&n.Subflow is not null?n with{Subflow=Display(n.Subflow,prefix+n.Id+"/")}:
            taskMap.TryGetValue(prefix+n.Id,out var task)?n with{Label=task.Title,Task=n.Task! with{Title=task.Title,DefaultAssignee=task.AssignedTo,ActivityType=task.ActivityType??n.Task!.ActivityType}}:n).ToArray()};
        tx.Commit(); return new(job, template, version, revision, Display(customGraph,""), customEvaluation, customTasks.ToArray(), history.ToArray());
    }
    public static void GuardTask(NpgsqlConnection c, NpgsqlTransaction? tx, Guid activity, string requested)
    {
        Guid job; string? id; string current;
        using (var q = Command(c, tx, "select job_id,workflow_node_id,status from studio_job_activities where id=$1 and (workflow_node_id is not null or (template_step_key is not null and work_order_id is null and exists(select 1 from studio_job_template_instances i where i.job_id=studio_job_activities.job_id)))", activity))
        { using var r = q.ExecuteReader(); if (!r.Read()) return; job = r.GetGuid(0); id = r.IsDBNull(1)?null:r.GetString(1); current = r.GetString(2); }
        if(id is null){if(requested!=current)throw new ArgumentException("Work preserved from an earlier template cannot be reopened. Add a correction task instead.");return;}
        if (current == "Completed" && requested != "Completed") throw new ArgumentException("Completed workflow work is immutable. Add a correction task instead.");
        if (current == "Completed" && requested == "Completed") return;
        var def = Definition(c, tx, job)!; var graph = Graph(def); var n = WorkflowGraphEngine.Flatten(graph).Nodes.Single(n => n.Id == id);
        if (requested == "Archived") throw new ArgumentException("Workflow tasks cannot be silently archived.");
        if (requested == "Cancelled" && n.Task?.Optional != true) throw new ArgumentException("Only optional workflow tasks may be skipped.");
        if (requested is "InProgress" or "Completed" or "Partial" or "ReadyForDelivery")
        {
            var states = Statuses(c, tx, job); states[id] = "NotStarted";
            var state = WorkflowGraphEngine.Evaluate(graph, states, Services(c, tx, job)).Nodes.Single(n => n.Id == id);
            bool preservedStartedWork = state.Status == "Inactive" && current is not ("NotStarted" or "Scheduled" or "Cancelled");
            if (state.Status != "Available" && !preservedStartedWork) throw new ArgumentException(state.Reason ?? "This task is not eligible.");
        }
    }
    public static void GuardCompletion(NpgsqlConnection c, NpgsqlTransaction? tx, Guid job)
    {
        var def = Definition(c, tx, job);
        if (def is not null && !WorkflowGraphEngine.Evaluate(Graph(def), Statuses(c, tx, job), Services(c, tx, job)).Completed)
            throw new ArgumentException("Complete all applicable workflow tasks before delivery or closing.");
    }
    public static void GuardProductionReady(NpgsqlConnection c,NpgsqlTransaction? tx,Guid job)
    {
        var def=Definition(c,tx,job);if(def is null)return;
        var nodes=WorkflowGraphEngine.Flatten(Graph(def)).Nodes.ToDictionary(n=>n.Id);
        var evaluation=WorkflowGraphEngine.Evaluate(Graph(def),Statuses(c,tx,job),Services(c,tx,job));
        if(evaluation.Nodes.Any(s=>nodes[s.Id].Kind=="Task"&&nodes[s.Id].Task!.ActivityType is not ("Delivery" or "Archive")&&s.Status is not ("Completed" or "Skipped" or "Inactive")))
            throw new ArgumentException("Complete all applicable production workflow tasks before submission or delivery.");
    }
    public static void GuardSourceReady(NpgsqlConnection c,NpgsqlTransaction? tx,Guid job)
    {
        var def=Definition(c,tx,job);if(def is null)return;
        var nodes=WorkflowGraphEngine.Flatten(Graph(def)).Nodes.ToDictionary(n=>n.Id);
        if(WorkflowGraphEngine.Evaluate(Graph(def),Statuses(c,tx,job),Services(c,tx,job)).Nodes.Any(s=>
            (nodes[s.Id].Task?.ActivityType is "FileIngest" or "Backup")&&s.Status is not ("Completed" or "Skipped" or "Inactive")))
            throw new ArgumentException("Complete the active workflow ingest and backup tasks before source submission.");
    }
    public static void CompleteMilestone(NpgsqlConnection c,NpgsqlTransaction tx,Guid job,string type)
    {
        var def=Definition(c,tx,job);if(def is null)return;
        var nodes=WorkflowGraphEngine.Flatten(Graph(def)).Nodes.ToDictionary(n=>n.Id);
        // Reevaluate between passes for multiple connected tasks fulfilled by the same domain event.
        for(int pass=0;pass<nodes.Count;pass++)
        {
            var eligible=WorkflowGraphEngine.Evaluate(Graph(def),Statuses(c,tx,job),Services(c,tx,job)).Nodes
                .Where(s=>nodes[s.Id].Task?.ActivityType==type&&(s.Status is "Available" or "InProgress")).Select(s=>s.Id).ToArray();
            if(eligible.Length==0)return;
            foreach(var id in eligible)Execute(c,tx,"update studio_job_activities set status='Completed',started_at_utc=coalesce(started_at_utc,now()),completed_at_utc=coalesce(completed_at_utc,now()),updated_at_utc=now() where job_id=$1 and workflow_node_id=$2",job,id);
        }
    }
    internal static long Lock(NpgsqlConnection c, NpgsqlTransaction tx, Guid job, long expected)
    {
        using var q = Command(c, tx, "select workflow_revision,status from studio_jobs where id=$1 for update", job); using var r = q.ExecuteReader();
        if (!r.Read()) throw new KeyNotFoundException("Job was not found.");
        if (r.GetString(1) is "Completed" or "Cancelled" or "Delivered") throw new ArgumentException("Job is closed for workflow changes.");
        long revision = r.GetInt64(0); if (revision != expected) throw new InvalidOperationException("Job changed. Refresh its impact preview."); return revision;
    }
    internal static bool Replay(NpgsqlConnection c, NpgsqlTransaction tx, Guid job, Guid operation, object request, string actor)
    {
        if (operation == Guid.Empty) throw new ArgumentException("Operation ID is required.");
        string payload = JsonSerializer.Serialize(request, Json);
        using var q = Command(c, tx, "select payload=$3::jsonb and actor=$4 from studio_job_workflow_operations where job_id=$1 and operation_id=$2", job, operation, payload, actor);
        if (q.ExecuteScalar() is bool same) { if (!same) throw new ArgumentException("Operation ID was already used for a different change."); return true; }
        return false;
    }
    internal static void Record(NpgsqlConnection c, NpgsqlTransaction tx, Guid job, Guid operation, object request, string actor, string action)
    {
        Execute(c, tx, "insert into studio_job_workflow_operations(job_id,operation_id,payload,actor) values($1,$2,$3::jsonb,$4)", job, operation, JsonSerializer.Serialize(request, Json), actor);
        Execute(c, tx, "update studio_jobs set workflow_revision=workflow_revision+1 where id=$1", job);
        Execute(c, tx, "insert into studio_operational_audit(id,job_id,action,user_name,detail,created_at_utc) values($1,$2,$3,$4,$5,now())", Guid.NewGuid(), job, action, actor, JsonSerializer.Serialize(request, Json));
    }
    public TemplateImpact Impact(Guid job, Guid template)
    { using var c = database.OpenConnection(); using var tx = c.BeginTransaction(System.Data.IsolationLevel.RepeatableRead); return Impact(c, tx, job, CommercialTemplateStore.Get(c, tx, template) ?? throw new KeyNotFoundException()); }
    internal static TemplateImpact Impact(NpgsqlConnection c, NpgsqlTransaction tx, Guid job, TemplateVersion target)
    {
        var previous = Definition(c, tx, job); var before = previous is null ? new Dictionary<string, WorkflowNode>() : WorkflowGraphEngine.Flatten(Graph(previous)).Nodes.Where(n => n.Kind == "Task").ToDictionary(n => n.Id);
        var after = WorkflowGraphEngine.Flatten(Graph(target.Definition)).Nodes.Where(n => n.Kind == "Task").ToDictionary(n => n.Id);
        var states = Statuses(c, tx, job); var remain = before.Keys.Intersect(after.Keys).ToArray(); var removed = before.Keys.Except(after.Keys).ToArray();
        string[] dependencies(string id, CommercialTemplate def) => WorkflowGraphEngine.Flatten(Graph(def)).Edges.Where(e => e.To == id).Select(e => JsonSerializer.Serialize(e with { Id = "" }, Json)).Order().ToArray();
        var assignment = remain.Where(id => before[id].Role != after[id].Role || before[id].Task?.DefaultAssignee != after[id].Task?.DefaultAssignee).ToArray();
        var changed = remain.Where(id => !dependencies(id, previous!).SequenceEqual(dependencies(id, target.Definition))).ToArray();
        var affected = states.Where(s => s.Value == "Completed" && (removed.Contains(s.Key) || changed.Contains(s.Key) || (after.TryGetValue(s.Key, out var n) && JsonSerializer.Serialize(n.Task, Json) != JsonSerializer.Serialize(before[s.Key].Task, Json)))).Select(s => s.Key).ToArray();
        return new(remain, after.Keys.Except(before.Keys).ToArray(), removed, affected, assignment, changed,
            states.Any(s => s.Value != "NotStarted" && s.Value != "Scheduled"), "Posted charges, invoices and payments remain unchanged. Adjust billing separately through POS.", target.Version, before.Values.Concat(after.Values).GroupBy(n=>n.Id).ToDictionary(g=>g.Key,g=>g.Last().Label));
    }
    public void Apply(Guid job, ApplyJobTemplateRequest request, string actor)
    {
        using var c = database.OpenConnection(); using var tx = c.BeginTransaction();
        // Lock before replay, so concurrent retries see the committed operation.
        using (var q = Command(c, tx, "select id from studio_jobs where id=$1 for update", job)) if (q.ExecuteScalar() is null) throw new KeyNotFoundException();
        if (Replay(c, tx, job, request.OperationId, request, actor)) return;
        Lock(c, tx, job, request.ExpectedRevision);
        var target = CommercialTemplateStore.Get(c, tx, request.TemplateId) ?? throw new KeyNotFoundException("Template was not found.");
        if (!target.Active || target.Kind != "Job") throw new ArgumentException("Choose a published active Job Template.");
        if (request.ExpectedTemplateVersion.HasValue && request.ExpectedTemplateVersion != target.Version) throw new InvalidOperationException("The published template changed. Review its impact again.");
        var impact = Impact(c, tx, job, target);
        if (impact.RequiresConfirmation && (!request.ConfirmChanges || string.IsNullOrWhiteSpace(request.Reason))) throw new ArgumentException("Review the impact and confirm the template change with a reason.");
        Execute(c, tx, "insert into studio_job_workflow_history(id,job_id,definition,template_id,template_version,actor,reason) select $1,job_id,definition,template_id,template_version,$3,$4 from studio_job_template_instances where job_id=$2", Guid.NewGuid(), job, actor, request.Reason ?? "Template replaced before work began");
        foreach (var id in impact.Removed)
            Execute(c, tx, "update studio_job_activities set workflow_node_id=null,status=case when status='Completed' then status else 'Cancelled' end,updated_at_utc=now() where job_id=$1 and workflow_node_id=$2", job, id);
        Execute(c, tx, """
            insert into studio_job_template_instances(job_id,template_id,template_version,definition,operation_id,created_by)
            values($1,$2,$3,$4::jsonb,$5,$6) on conflict(job_id) do update set template_id=excluded.template_id,template_version=excluded.template_version,
              definition=excluded.definition,operation_id=excluded.operation_id,created_by=excluded.created_by,created_at=now()
            """, job, target.Id, target.Version, JsonSerializer.Serialize(target.Definition, Json), request.OperationId, actor);
        Instantiate(c, tx, job, target.Definition, actor);
        // Agreements are operational service selections, never financial transaction deletion.
        Execute(c, tx, "update studio_job_catalog_agreements set workflow_selected=false where job_id=$1", job);
        foreach (var p in target.Definition.Products ?? []) SelectService(c, tx, job, p.CatalogItemId, p.Quantity, p.Specifications);
        Record(c, tx, job, request.OperationId, request, actor, "JobWorkflowTemplateChanged"); tx.Commit();
    }
    private static void SelectService(NpgsqlConnection c, NpgsqlTransaction tx, Guid job, Guid product, int quantity = 1, string? specifications = null)
    {
        using var q = Command(c, tx, "select id from studio_job_catalog_agreements where job_id=$1 and catalog_item_id=$2 order by confirmed_at desc nulls last limit 1", job, product);
        if (q.ExecuteScalar() is Guid id) Execute(c, tx, "update studio_job_catalog_agreements set workflow_selected=true,quantity=case when confirmed_at is null then $2 else quantity end,specifications=case when confirmed_at is null then $3 else specifications end where id=$1", id, quantity, specifications);
        else Execute(c, tx, "insert into studio_job_catalog_agreements(id,job_id,catalog_item_id,quantity,specifications) values($1,$2,$3,$4,$5)", Guid.NewGuid(), job, product, quantity, specifications);
    }
    public void SetServices(Guid job, WorkflowServicesRequest request, string actor)
    {
        using var c = database.OpenConnection(); using var tx = c.BeginTransaction();
        using (var q = Command(c, tx, "select id from studio_jobs where id=$1 for update", job)) if (q.ExecuteScalar() is null) throw new KeyNotFoundException();
        if (Replay(c, tx, job, request.OperationId, request, actor)) return;
        Lock(c, tx, job, request.ExpectedRevision); var def = Definition(c, tx, job) ?? throw new ArgumentException("Apply a Job Template first.");
        if (request.CatalogItemIds.Length > 300) throw new ArgumentException("Too many services.");
        foreach (var id in request.CatalogItemIds)
        { using var q = Command(c, tx, "select exists(select 1 from studio_pos_catalog where id=$1 and is_active)", id); if (!(bool)q.ExecuteScalar()!) throw new ArgumentException("Choose active catalog services."); }
        var states = Statuses(c, tx, job); var existing = Services(c, tx, job); var selected = request.CatalogItemIds.ToHashSet();
        var old = WorkflowGraphEngine.Evaluate(Graph(def), new Dictionary<string, string>(), existing);
        var next = WorkflowGraphEngine.Evaluate(Graph(def), new Dictionary<string, string>(), selected);
        bool startedRemoved = old.Nodes.Any(n => n.Status != "Inactive" && next.Nodes.Any(x => x.Id == n.Id && x.Status == "Inactive") && states.TryGetValue(n.Id, out var status) && status is not ("NotStarted" or "Scheduled" or "Cancelled"));
        if (startedRemoved && (!request.ConfirmChanges || string.IsNullOrWhiteSpace(request.Reason))) throw new ArgumentException("A removed service has started work. Confirm with a reason; work and history will be preserved.");
        Execute(c, tx, "update studio_job_catalog_agreements set workflow_selected=false where job_id=$1", job);
        foreach (var id in selected) SelectService(c, tx, job, id);
        Record(c, tx, job, request.OperationId, request, actor, "JobWorkflowServicesChanged"); tx.Commit();
    }
    public void TransitionTask(Guid job, GraphTaskCommand request, string actor)
    {
        if (string.IsNullOrWhiteSpace(request.NodeId) || request.NodeId.Length > 120) throw new ArgumentException("A workflow task is required.");
        if (request.Action is not ("start" or "complete" or "skip")) throw new ArgumentException("Choose start, complete or skip.");
        if (request.Action == "skip" && string.IsNullOrWhiteSpace(request.Reason)) throw new ArgumentException("A reason is required to skip a workflow task.");
        using var c = database.OpenConnection(); using var tx = c.BeginTransaction();
        using (var q = Command(c, tx, "select id from studio_jobs where id=$1 for update", job)) if (q.ExecuteScalar() is null) throw new KeyNotFoundException();
        if (Replay(c, tx, job, request.OperationId, request, actor)) { tx.Commit(); return; }
        Lock(c, tx, job, request.ExpectedRevision);
        var definition = Definition(c, tx, job);
        WorkflowGraph graph;
        if (definition is null)
        {
            EnsureDefaultActivities(c, tx, job, actor);
            graph = WorkflowGraphEngine.FromSteps(DefaultSteps());
        }
        else graph = Graph(definition);
        var task = WorkflowGraphEngine.Flatten(graph).Nodes.SingleOrDefault(n => n.Id == request.NodeId && n.Kind == "Task")
            ?? throw new ArgumentException("The workflow task was not found.");
        var evaluation = WorkflowGraphEngine.Evaluate(graph, Statuses(c, tx, job), Services(c, tx, job));
        var state = evaluation.Nodes.Single(n => n.Id == request.NodeId);
        var current = Statuses(c, tx, job).GetValueOrDefault(request.NodeId, "NotStarted");
        if (request.Action == "start")
        {
            if (state.Status is not ("Available" or "InProgress")) throw new ArgumentException(state.Reason ?? "This workflow task is not available.");
            if (current == "InProgress") { tx.Commit(); return; }
            Execute(c, tx, "update studio_job_activities set status='InProgress',started_at_utc=coalesce(started_at_utc,now()),updated_at_utc=now() where job_id=$1 and workflow_node_id=$2", job, request.NodeId);
        }
        else if (request.Action == "complete")
        {
            if (state.Status is not ("Available" or "InProgress")) throw new ArgumentException(state.Reason ?? "This workflow task is not available.");
            Execute(c, tx, "update studio_job_activities set status='Completed',started_at_utc=coalesce(started_at_utc,now()),completed_at_utc=now(),updated_at_utc=now() where job_id=$1 and workflow_node_id=$2", job, request.NodeId);
        }
        else
        {
            if (!task.Task!.Optional) throw new ArgumentException("Only optional workflow tasks can be skipped.");
            if (current is "Completed" or "Cancelled") throw new ArgumentException("This workflow task is already closed.");
            Execute(c, tx, "update studio_job_activities set status='Cancelled',completed_at_utc=now(),updated_at_utc=now() where job_id=$1 and workflow_node_id=$2", job, request.NodeId);
        }
        Record(c, tx, job, request.OperationId, request, actor, "JobWorkflowTask" + char.ToUpperInvariant(request.Action[0]) + request.Action[1..]);
        tx.Commit();
    }
}
