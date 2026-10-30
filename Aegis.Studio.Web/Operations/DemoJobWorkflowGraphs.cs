using System.Text.Json;
using Aegis.Studio.Web.Production;
using Npgsql;
using static Aegis.Studio.Web.Production.CommercialTemplateStore;

namespace Aegis.Studio.Web.Operations;

/// <summary>Adds graph scenarios to the existing comprehensive demo; leaves its genuine media intact.</summary>
internal static class DemoJobWorkflowGraphs
{
    internal static string TemplateName(string title)
    {
        if (title.Contains("Wedding",StringComparison.OrdinalIgnoreCase)||title.Contains("Engagement",StringComparison.OrdinalIgnoreCase)) return "Wedding Photography";
        if (title.Contains("Graduation",StringComparison.OrdinalIgnoreCase)||title.Contains("School",StringComparison.OrdinalIgnoreCase)||title.Contains("Class",StringComparison.OrdinalIgnoreCase)) return "Graduation Photography";
        if (title.Contains("Headshot",StringComparison.OrdinalIgnoreCase)||title.Contains("Corporate",StringComparison.OrdinalIgnoreCase)||title.Contains("Executive",StringComparison.OrdinalIgnoreCase)) return "Corporate Headshots";
        if (title.Contains("Passport",StringComparison.OrdinalIgnoreCase)||title.Contains("Visa",StringComparison.OrdinalIgnoreCase)||title.Contains("Employee ID",StringComparison.OrdinalIgnoreCase)) return "ID Photography";
        if (title.Contains("Restoration",StringComparison.OrdinalIgnoreCase)) return "Photo Restoration";
        if (title.Contains("Printing",StringComparison.OrdinalIgnoreCase)) return "Photo Printing";
        return "Portrait Photography";
    }

    internal static void Seed(NpgsqlConnection c, NpgsqlTransaction tx, string actor)
    {
        Guid Service(string code, string name, string category)
        {
            using var q = Command(c, tx, "select id from studio_pos_catalog where code=$1", code);
            if (q.ExecuteScalar() is Guid found) return found;
            Guid id = Guid.NewGuid();
            Execute(c, tx, "insert into studio_pos_catalog(id,code,name,category,unit_price,is_active,sort_order,updated_at_utc) values($1,$2,$3,$4,500,true,0,now())", id, code, name, category); return id;
        }
        Guid retouch = Service("GRAPH-RETOUCH", "Professional retouching", "Production"), print = Service("GRAPH-PRINT", "Printed photographs", "Prints");
        Guid WorkOrder(string name,string type,string area)
        {
            using(var existing=Command(c,tx,"select id from studio_commercial_templates where kind='WorkOrder' and definition->>'name'=$1 order by updated_at limit 1",name))
                if(existing.ExecuteScalar() is Guid found)return found;
            var definition=new CommercialTemplate(name,"Demo production and mandatory quality control",[
                new("produce",name,type,area),new("qc","Quality inspection","QC","Quality Control",DependsOn:["produce"],QualityControl:true)]);
            CommercialRules.Validate(definition,"WorkOrder");Guid id=Guid.NewGuid();string json=JsonSerializer.Serialize(definition,Json);
            Execute(c,tx,"insert into studio_commercial_templates(id,kind,version,active,definition,updated_by) values($1,'WorkOrder',1,true,$2::jsonb,$3)",id,json,actor);
            Execute(c,tx,"insert into studio_commercial_template_versions(template_id,version,definition,created_by) values($1,1,$2::jsonb,$3)",id,json,actor);
            return id;
        }
        var retouchOrder=WorkOrder("Retouching and Quality Control","Retouch","Editing");
        var printOrder=WorkOrder("Printing and Quality Control","Print","Printing");
        Execute(c,tx,"update studio_pos_catalog set requires_production=true,work_order_template_id=$2 where id=$1",retouch,retouchOrder);
        Execute(c,tx,"update studio_pos_catalog set requires_production=true,work_order_template_id=$2 where id=$1",print,printOrder);
        using (var q = Command(c, tx, "select exists(select 1 from studio_state where key='demo-job-workflow-graphs-v1')")) if ((bool)q.ExecuteScalar()!) return;
        var conditional = WorkflowGraphExamples.Graduation(retouch, print); var parallel = WorkflowGraphExamples.Parallel(); var linear = WorkflowGraphExamples.Linear();
        var names = new[] { "Graduation Photography", "Wedding Photography", "Corporate Headshots", "ID Photography", "Portrait Photography", "Photo Printing", "Photo Restoration", "Image Editing" };
        var templates = new List<TemplateVersion>();
        for (int i = 0; i < names.Length; i++)
        {
            var graph = i < 5 ? conditional : i == 6 ? parallel : linear;
            var definition = JobGraphStore.Normalize(new(names[i], "Graph-based Studio demo template", [], Workflow: graph,
                SchedulingRequired: i < 5, Deliverables: ["Client-approved digital or physical deliverables"], CompletionRequirements: "Complete all active branches and quality approval."));
            CommercialRules.Validate(definition, "Job"); Guid id = Guid.NewGuid(); string json = JsonSerializer.Serialize(definition, Json);
            Execute(c, tx, "insert into studio_commercial_templates(id,kind,version,active,definition,updated_by) values($1,'Job',1,true,$2::jsonb,$3)", id, json, actor);
            Execute(c, tx, "insert into studio_commercial_template_versions(template_id,version,definition,created_by) values($1,1,$2::jsonb,$3)", id, json, actor);
            templates.Add(new(id, "Job", 1, true, definition));
        }
        int[] jobNumbers = [101,201,203,301,303,401,501,502,601,602];
        for (int i = 0; i < jobNumbers.Length; i++)
        {
            Guid job = DemoDataExpansion.Id(41, jobNumbers[i]);
            using (var check = Command(c, tx, "select exists(select 1 from studio_jobs where id=$1)", job)) if (!(bool)check.ExecuteScalar()!) continue;
            using (var check = Command(c, tx, "select exists(select 1 from studio_job_template_instances where job_id=$1)", job)) if ((bool)check.ExecuteScalar()!) continue;
            using var title = Command(c,tx,"select title from studio_jobs where id=$1",job);
            var template = templates.Single(t=>t.Definition.Name==TemplateName((string)title.ExecuteScalar()!));
            ApplyNewJob(c, tx, job, template, Guid.NewGuid(), actor);
            if (i is 2 or 4 or 6 or 7 or 8 or 9) Execute(c, tx, "insert into studio_job_catalog_agreements(id,job_id,catalog_item_id,quantity) values($1,$2,$3,1)", Guid.NewGuid(), job, retouch);
            if (i is 3 or 4 or 6 or 7 or 8 or 9) Execute(c, tx, "insert into studio_job_catalog_agreements(id,job_id,catalog_item_id,quantity) values($1,$2,$3,1)", Guid.NewGuid(), job, print);
            if (i >= 6) Execute(c, tx, "update studio_job_activities set status='Completed',completed_at_utc=now(),started_at_utc=now(),assigned_to=$2 where job_id=$1 and workflow_node_id in ('review','capture/shoot','capture/ingest')", job, actor);
            if (i == 6) Execute(c, tx, "update studio_job_activities set status='InProgress',started_at_utc=now(),assigned_to=$2 where job_id=$1 and workflow_node_id='retouch'", job, actor);
            if (i == 7) Execute(c, tx, "update studio_job_activities set status='Blocked',blocked_reason='Awaiting client selection',assigned_to=$2 where job_id=$1 and workflow_node_id='digital'", job, actor);
            if (i == 8) Execute(c, tx, "update studio_job_activities set status='Completed',completed_at_utc=now(),started_at_utc=now(),assigned_to=$2 where job_id=$1 and workflow_node_id is not null", job, actor);
            if (i == 9)
            {
                Execute(c, tx, "insert into studio_job_workflow_history(id,job_id,definition,template_id,template_version,actor,reason) values($1,$2,$3::jsonb,$4,1,$5,'Template changed after completed consultation; completed work preserved')", Guid.NewGuid(), job, JsonSerializer.Serialize(templates[5].Definition, Json), templates[5].Id, actor);
                Execute(c, tx, "insert into studio_job_activities(id,job_id,activity_type,title,status,priority,assigned_to,created_by,created_at_utc,updated_at_utc,completed_at_utc,template_step_key) values($1,$2,'Other','Completed consultation from previous template','Completed','Normal',$3,$3,now(),now(),now(),'previous-consultation')", Guid.NewGuid(), job, actor);
            }
        }
        Execute(c, tx, "insert into studio_state(key,value,updated_at_utc) values('demo-job-workflow-graphs-v1','seeded',now()) on conflict do nothing");
    }
}

