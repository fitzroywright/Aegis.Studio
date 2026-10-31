using System.Text.Json;
using Aegis.Studio.Web.Production;
using Npgsql;
using static Aegis.Studio.Web.Production.CommercialTemplateStore;

namespace Aegis.Studio.Web.Operations;

/// <summary>Adds graph scenarios to the existing comprehensive demo; leaves its genuine media intact.</summary>
internal static class DemoJobWorkflowGraphs
{
    private sealed record DemoProduct(string Code,string Name,string Category,decimal Price,decimal Cost,string Description,Dictionary<string,string[]> Options,bool RequiresProduction=true);

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
        SeedRetailProducts(c,tx,printOrder);
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

    private static void SeedRetailProducts(NpgsqlConnection c,NpgsqlTransaction tx,Guid printOrder)
    {
        DemoProduct[] products =
        [
            new("DEMO-PRINT-4X6","4×6 Photographic Print","Prints",250m,70m,"Professional photographic print on archival paper.",new(){{"Finish",["Glossy","Matte"]}}),
            new("DEMO-PRINT-5X7","5×7 Photographic Print","Prints",450m,120m,"Professional photographic print for albums, desks and gifts.",new(){{"Finish",["Glossy","Matte"]}}),
            new("DEMO-PRINT-8X10","8×10 Portrait Print","Prints",1200m,300m,"Classic portrait enlargement on archival photographic paper.",new(){{"Finish",["Glossy","Matte","Lustre"]}}),
            new("DEMO-PRINT-11X14","11×14 Portrait Print","Prints",2500m,650m,"Large portrait enlargement suitable for framing.",new(){{"Finish",["Matte","Lustre"]}}),
            new("DEMO-PRINT-16X20","16×20 Portrait Print","Prints",4800m,1300m,"Display-size portrait enlargement with colour correction.",new(){{"Finish",["Matte","Lustre"]}}),
            new("DEMO-POSTER-20X30","20×30 Photographic Poster","Prints",7500m,2200m,"Large-format photographic poster for events and displays.",new(){{"Finish",["Matte","Glossy"]}}),
            new("DEMO-CANVAS-12X18","12×18 Gallery-Wrap Canvas","Wall Art",9500m,3500m,"Ready-to-hang canvas with a gallery-wrapped edge.",new(){{"Edge",["Image wrap","Black","White"]}}),
            new("DEMO-CANVAS-20X30","20×30 Gallery-Wrap Canvas","Wall Art",18000m,7000m,"Large ready-to-hang canvas wall portrait.",new(){{"Edge",["Image wrap","Black","White"]}}),
            new("DEMO-FRAME-8X10","Framed 8×10 Portrait","Wall Art",5500m,2000m,"Finished 8×10 portrait supplied in a tabletop or wall frame.",new(){{"Frame",["Black","White","Natural wood"]},{"Mount",["None","White","Cream"]}}),
            new("DEMO-FRAME-11X14","Framed 11×14 Portrait","Wall Art",8500m,3200m,"Finished 11×14 wall portrait with frame and optional mount.",new(){{"Frame",["Black","White","Natural wood"]},{"Mount",["White","Cream"]}}),
            new("DEMO-BOOK-8X8","8×8 Hardcover Photo Book","Albums",18000m,7500m,"Twenty-page hardcover photo book with professionally designed spreads.",new(){{"Cover",["Black","White","Photo cover"]},{"Extra pages",["None","10 pages","20 pages"]}}),
            new("DEMO-ALBUM-12X12","12×12 Premium Wedding Album","Albums",45000m,20000m,"Premium lay-flat wedding album with twenty designed pages.",new(){{"Cover",["Leatherette","Linen","Photo cover"]},{"Colour",["Black","White","Burgundy"]}}),
            new("DEMO-GRAD-PACK","Graduation Portrait Package","Packages",6500m,1800m,"One 8×10, two 5×7 and eight wallet-size graduation prints.",new(){{"Finish",["Glossy","Matte"]}}),
            new("DEMO-ID-SET","Passport and ID Photo Set","Packages",1500m,200m,"Set of four compliant passport or identification photographs.",new(){{"Standard",["Jamaica passport","US visa","Canadian visa","Custom ID"]}}),
            new("DEMO-DIGITAL-ONE","Full-Resolution Digital Image","Digital",1200m,50m,"One retouched high-resolution image delivered electronically.",new(),false),
            new("DEMO-DIGITAL-GALLERY","Full-Resolution Digital Gallery","Digital",15000m,500m,"Downloadable collection of the final retouched photographs.",new(),false),
            new("DEMO-USB","Studio USB Presentation Drive","Keepsakes",2500m,900m,"Branded USB drive prepared with the client's delivered images.",new(){{"Capacity",["16 GB","32 GB"]}}),
            new("DEMO-CARDS-20","Twenty Photo Greeting Cards","Cards & Stationery",8000m,3000m,"Set of twenty folded photo cards with envelopes.",new(){{"Occasion",["Thank you","Christmas","Birthday","Custom"]},{"Finish",["Matte","Glossy"]}}),
            new("DEMO-MEMORIAL-50","Fifty Memorial Programmes","Cards & Stationery",15000m,6000m,"Fifty professionally designed and printed memorial programmes.",new(){{"Pages",["4 pages","8 pages"]},{"Finish",["Matte","Glossy"]}}),
            new("DEMO-LAMINATE","Photograph Lamination","Finishing",500m,120m,"Protective lamination for one photograph up to 8×10.",new(){{"Finish",["Gloss","Matte"]}})
        ];
        for(int index=0;index<products.Length;index++)
        {
            DemoProduct product=products[index];
            using var find=Command(c,tx,"select id from studio_pos_catalog where code=$1",product.Code);
            Guid id=find.ExecuteScalar() is Guid existing?existing:DemoDataExpansion.Id(52,index+1);
            Execute(c,tx,"""
                insert into studio_pos_catalog
                  (id,code,name,category,unit_price,is_active,sort_order,updated_at_utc,description,estimated_cost,tax_rate,options,requires_production,work_order_template_id)
                values($1,$2,$3,$4,$5,true,$6,now(),$7,$8,15,$9::jsonb,$10,$11)
                on conflict(id) do update set
                  code=excluded.code,name=excluded.name,category=excluded.category,unit_price=excluded.unit_price,
                  is_active=true,sort_order=excluded.sort_order,updated_at_utc=now(),description=excluded.description,
                  estimated_cost=excluded.estimated_cost,tax_rate=excluded.tax_rate,options=excluded.options,
                  requires_production=excluded.requires_production,work_order_template_id=excluded.work_order_template_id
                """,id,product.Code,product.Name,product.Category,product.Price,100+index,product.Description,
                product.Cost,JsonSerializer.Serialize(product.Options,Json),product.RequiresProduction,
                product.RequiresProduction?printOrder:DBNull.Value);
        }
    }

}

