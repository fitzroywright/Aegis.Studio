using System.Security.Claims;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Microsoft.AspNetCore.Authorization;
using Npgsql;

namespace Aegis.Studio.Web.Production;

public sealed class ProductionConfigurationStore
{
    private readonly StudioDatabase database;
    public ProductionConfigurationStore(StudioDatabase database){this.database=database;}

    public void EnsureSchema()
    {
        using var c=database.OpenConnection();using var cmd=c.CreateCommand();
        cmd.CommandText="""
        create table if not exists studio_task_templates(
          id uuid primary key,name text not null,description text null,activity_type text not null default 'Other',
          expected_effort_minutes integer not null default 60 check(expected_effort_minutes>=0),
          required_role text null,required_skill text null,default_assignee text null,
          priority text not null default 'Normal',requires_scheduling boolean not null default false,
          due_offset_hours integer null,is_mandatory boolean not null default true,is_active boolean not null default true,
          created_at_utc timestamptz not null default now(),updated_at_utc timestamptz not null default now()
        );
        create unique index if not exists ux_studio_task_templates_name on studio_task_templates(lower(name));

        create table if not exists studio_production_recipes(
          id uuid primary key,name text not null,description text null,is_active boolean not null default true,
          created_at_utc timestamptz not null default now(),updated_at_utc timestamptz not null default now()
        );
        create unique index if not exists ux_studio_production_recipes_name on studio_production_recipes(lower(name));

        create table if not exists studio_production_recipe_steps(
          id uuid primary key,recipe_id uuid not null references studio_production_recipes(id) on delete cascade,
          task_template_id uuid not null references studio_task_templates(id),
          sequence_order integer not null default 0,is_mandatory boolean not null default true,
          default_assignee text null,required_role text null,required_skill text null,
          due_offset_hours integer null,depends_on_step_id uuid null references studio_production_recipe_steps(id) on delete set null,
          unique(recipe_id,sequence_order)
        );
        create index if not exists ix_studio_recipe_steps_recipe on studio_production_recipe_steps(recipe_id,sequence_order);

        create table if not exists studio_job_types(
          id uuid primary key,name text not null,description text null,category text null,
          default_recipe_id uuid null references studio_production_recipes(id) on delete set null,
          is_active boolean not null default true,created_at_utc timestamptz not null default now(),updated_at_utc timestamptz not null default now()
        );
        create unique index if not exists ux_studio_job_types_name on studio_job_types(lower(name));

        alter table studio_jobs add column if not exists job_type_id uuid null references studio_job_types(id) on delete set null;
        alter table studio_jobs add column if not exists production_recipe_id uuid null references studio_production_recipes(id) on delete set null;
        alter table studio_job_activities add column if not exists expected_effort_minutes integer null;
        alter table studio_job_activities add column if not exists task_template_id uuid null references studio_task_templates(id) on delete set null;
        alter table studio_job_activities add column if not exists recipe_step_id uuid null references studio_production_recipe_steps(id) on delete set null;
        alter table studio_job_activities add column if not exists sequence_order integer null;
        alter table studio_job_activities add column if not exists requires_scheduling boolean not null default false;
        alter table studio_job_activities add column if not exists assignment_warning text null;
        create index if not exists ix_studio_job_activities_recipe on studio_job_activities(job_id,recipe_step_id);
        """;
        cmd.ExecuteNonQuery();
        SeedDefaults();
    }

    private void SeedDefaults()
    {
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        var templates=new[]{
          new {Name="Import images",Type="FileIngest",Minutes=60,Role=(string?)null},
          new {Name="Verify backup",Type="Backup",Minutes=30,Role=(string?)null},
          new {Name="Cull and review",Type="Cull",Minutes=180,Role="Editor"},
          new {Name="Edit and color correct",Type="Edit",Minutes=240,Role="Editor"},
          new {Name="Retouch selected images",Type="Retouch",Minutes=180,Role="Editor"},
          new {Name="Prepare proofs",Type="Proof",Minutes=60,Role="Production"},
          new {Name="Print",Type="Print",Minutes=180,Role="Production"},
          new {Name="Package",Type="Other",Minutes=90,Role="Production"},
          new {Name="Quality control",Type="QC",Minutes=60,Role="Production Manager"},
          new {Name="Deliver",Type="Delivery",Minutes=60,Role="Production"},
          new {Name="Archive deliverables",Type="Archive",Minutes=30,Role=(string?)null}
        };
        var ids=new Dictionary<string,Guid>(StringComparer.OrdinalIgnoreCase);
        foreach(var t in templates)
        {
            Guid id=Deterministic("task-template:"+t.Name);ids[t.Name]=id;
            Exec(c,tx,"""insert into studio_task_templates(id,name,activity_type,expected_effort_minutes,required_role,is_active)
                         values($1,$2,$3,$4,$5,true) on conflict(id) do nothing""",id,t.Name,t.Type,t.Minutes,(object?)t.Role??DBNull.Value);
        }
        SeedRecipe(c,tx,"Wedding","Wedding photography production",
          ["Import images","Verify backup","Cull and review","Edit and color correct","Retouch selected images","Prepare proofs","Quality control","Deliver","Archive deliverables"],ids);
        SeedRecipe(c,tx,"Graduation","Graduation photography production",
          ["Import images","Verify backup","Cull and review","Edit and color correct","Print","Package","Quality control","Deliver","Archive deliverables"],ids);
        SeedRecipe(c,tx,"Portrait Session","Portrait production",
          ["Import images","Verify backup","Cull and review","Edit and color correct","Retouch selected images","Prepare proofs","Quality control","Deliver","Archive deliverables"],ids);
        SeedRecipe(c,tx,"Corporate Event","Corporate event production",
          ["Import images","Verify backup","Cull and review","Edit and color correct","Quality control","Deliver","Archive deliverables"],ids);
        SeedRecipe(c,tx,"School Photography","School photography production",
          ["Import images","Verify backup","Cull and review","Edit and color correct","Print","Package","Quality control","Deliver","Archive deliverables"],ids);
        tx.Commit();
    }

    private void SeedRecipe(NpgsqlConnection c,NpgsqlTransaction tx,string name,string description,string[] steps,Dictionary<string,Guid> templates)
    {
        Guid recipe=Deterministic("recipe:"+name);
        Exec(c,tx,"insert into studio_production_recipes(id,name,description,is_active) values($1,$2,$3,true) on conflict(id) do nothing",recipe,name,description);
        for(int i=0;i<steps.Length;i++)
        {
            Guid step=Deterministic($"recipe-step:{name}:{i}:{steps[i]}");
            Exec(c,tx,"""insert into studio_production_recipe_steps(id,recipe_id,task_template_id,sequence_order,is_mandatory)
                         values($1,$2,$3,$4,true) on conflict(id) do nothing""",step,recipe,templates[steps[i]],i+1);
        }
        Guid jobType=Deterministic("job-type:"+name);
        Exec(c,tx,"""insert into studio_job_types(id,name,description,category,default_recipe_id,is_active)
                     values($1,$2,$3,'Photography',$4,true) on conflict(id) do nothing""",jobType,name,description,recipe);
    }

    public IReadOnlyList<JobTypeDefinition> GetJobTypes(bool activeOnly=false)
    {
        using var c=database.OpenConnection();using var cmd=new NpgsqlCommand("""
          select jt.id,jt.name,jt.description,jt.category,jt.default_recipe_id,jt.is_active,r.name
          from studio_job_types jt left join studio_production_recipes r on r.id=jt.default_recipe_id
          where not $1 or jt.is_active order by jt.name
          """,c);cmd.Parameters.AddWithValue(activeOnly);
        using var r=cmd.ExecuteReader();var rows=new List<JobTypeDefinition>();
        while(r.Read())rows.Add(new(r.GetGuid(0),r.GetString(1),r.IsDBNull(2)?null:r.GetString(2),r.IsDBNull(3)?null:r.GetString(3),r.IsDBNull(4)?null:r.GetGuid(4),r.GetBoolean(5),r.IsDBNull(6)?null:r.GetString(6)));
        return rows;
    }

    public IReadOnlyList<TaskTemplateDefinition> GetTemplates(bool activeOnly=false)
    {
        using var c=database.OpenConnection();using var cmd=new NpgsqlCommand("""
          select id,name,description,activity_type,expected_effort_minutes,required_role,required_skill,default_assignee,priority,requires_scheduling,due_offset_hours,is_mandatory,is_active
          from studio_task_templates where not $1 or is_active order by name
          """,c);cmd.Parameters.AddWithValue(activeOnly);
        using var r=cmd.ExecuteReader();var rows=new List<TaskTemplateDefinition>();
        while(r.Read())rows.Add(new(r.GetGuid(0),r.GetString(1),r.IsDBNull(2)?null:r.GetString(2),r.GetString(3),r.GetInt32(4),r.IsDBNull(5)?null:r.GetString(5),r.IsDBNull(6)?null:r.GetString(6),r.IsDBNull(7)?null:r.GetString(7),r.GetString(8),r.GetBoolean(9),r.IsDBNull(10)?null:r.GetInt32(10),r.GetBoolean(11),r.GetBoolean(12)));
        return rows;
    }

    public IReadOnlyList<ProductionRecipeDefinition> GetRecipes(bool activeOnly=false)
    {
        using var c=database.OpenConnection();
        var recipes=new List<ProductionRecipeDefinition>();
        using(var cmd=new NpgsqlCommand("select id,name,description,is_active from studio_production_recipes where not $1 or is_active order by name",c))
        {
            cmd.Parameters.AddWithValue(activeOnly);using var r=cmd.ExecuteReader();
            while(r.Read())recipes.Add(new(r.GetGuid(0),r.GetString(1),r.IsDBNull(2)?null:r.GetString(2),r.GetBoolean(3),[]));
        }
        return recipes.Select(recipe=>recipe with{Steps=GetRecipeSteps(recipe.Id,c)}).ToList();
    }

    private static IReadOnlyList<ProductionRecipeStep> GetRecipeSteps(Guid recipeId,NpgsqlConnection c)
    {
        using var cmd=new NpgsqlCommand("""
          select s.id,s.recipe_id,s.task_template_id,s.sequence_order,s.is_mandatory,s.default_assignee,s.required_role,s.required_skill,s.due_offset_hours,s.depends_on_step_id,
                 t.name,t.description,t.activity_type,t.expected_effort_minutes,t.required_role,t.required_skill,t.default_assignee,t.priority,t.requires_scheduling,t.due_offset_hours,t.is_mandatory,t.is_active
          from studio_production_recipe_steps s join studio_task_templates t on t.id=s.task_template_id
          where s.recipe_id=$1 order by s.sequence_order
          """,c);cmd.Parameters.AddWithValue(recipeId);using var r=cmd.ExecuteReader();var rows=new List<ProductionRecipeStep>();
        while(r.Read())
        {
            var t=new TaskTemplateDefinition(r.GetGuid(2),r.GetString(10),r.IsDBNull(11)?null:r.GetString(11),r.GetString(12),r.GetInt32(13),r.IsDBNull(14)?null:r.GetString(14),r.IsDBNull(15)?null:r.GetString(15),r.IsDBNull(16)?null:r.GetString(16),r.GetString(17),r.GetBoolean(18),r.IsDBNull(19)?null:r.GetInt32(19),r.GetBoolean(20),r.GetBoolean(21));
            rows.Add(new(r.GetGuid(0),r.GetGuid(1),r.GetGuid(2),r.GetInt32(3),r.GetBoolean(4),r.IsDBNull(5)?null:r.GetString(5),r.IsDBNull(6)?null:r.GetString(6),r.IsDBNull(7)?null:r.GetString(7),r.IsDBNull(8)?null:r.GetInt32(8),r.IsDBNull(9)?null:r.GetGuid(9),t));
        }
        return rows;
    }

    public JobTypeDefinition SaveJobType(Guid? id,JobTypeRequest request)
    {
        if(string.IsNullOrWhiteSpace(request.Name))throw new ArgumentException("Job Type name is required.");
        Guid value=id??Guid.NewGuid();using var c=database.OpenConnection();using var cmd=new NpgsqlCommand("""
          insert into studio_job_types(id,name,description,category,default_recipe_id,is_active,created_at_utc,updated_at_utc)
          values($1,$2,$3,$4,$5,$6,now(),now())
          on conflict(id) do update set name=excluded.name,description=excluded.description,category=excluded.category,default_recipe_id=excluded.default_recipe_id,is_active=excluded.is_active,updated_at_utc=now()
          """,c);
        Add(cmd,value,request.Name.Trim(),Clean(request.Description),Clean(request.Category),request.DefaultRecipeId,request.IsActive);cmd.ExecuteNonQuery();
        return GetJobTypes().First(x=>x.Id==value);
    }

    public TaskTemplateDefinition SaveTemplate(Guid? id,TaskTemplateRequest request)
    {
        if(string.IsNullOrWhiteSpace(request.Name))throw new ArgumentException("Task Template name is required.");
        if(request.ExpectedEffortMinutes<0)throw new ArgumentException("Expected effort cannot be negative.");
        Guid value=id??Guid.NewGuid();using var c=database.OpenConnection();using var cmd=new NpgsqlCommand("""
          insert into studio_task_templates(id,name,description,activity_type,expected_effort_minutes,required_role,required_skill,default_assignee,priority,requires_scheduling,due_offset_hours,is_mandatory,is_active,created_at_utc,updated_at_utc)
          values($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,now(),now())
          on conflict(id) do update set name=excluded.name,description=excluded.description,activity_type=excluded.activity_type,expected_effort_minutes=excluded.expected_effort_minutes,
            required_role=excluded.required_role,required_skill=excluded.required_skill,default_assignee=excluded.default_assignee,priority=excluded.priority,requires_scheduling=excluded.requires_scheduling,
            due_offset_hours=excluded.due_offset_hours,is_mandatory=excluded.is_mandatory,is_active=excluded.is_active,updated_at_utc=now()
          """,c);
        Add(cmd,value,request.Name.Trim(),Clean(request.Description),Clean(request.ActivityType)??"Other",request.ExpectedEffortMinutes,Clean(request.RequiredRole),Clean(request.RequiredSkill),Clean(request.DefaultAssignee),Clean(request.Priority)??"Normal",request.RequiresScheduling,request.DueOffsetHours,request.IsMandatory,request.IsActive);cmd.ExecuteNonQuery();
        return GetTemplates().First(x=>x.Id==value);
    }

    public ProductionRecipeDefinition SaveRecipe(Guid? id,ProductionRecipeRequest request)
    {
        if(string.IsNullOrWhiteSpace(request.Name))throw new ArgumentException("Recipe name is required.");
        Guid value=id??Guid.NewGuid();using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        Exec(c,tx,"""insert into studio_production_recipes(id,name,description,is_active,created_at_utc,updated_at_utc) values($1,$2,$3,$4,now(),now())
                     on conflict(id) do update set name=excluded.name,description=excluded.description,is_active=excluded.is_active,updated_at_utc=now()""",value,request.Name.Trim(),(object?)Clean(request.Description)??DBNull.Value,request.IsActive);
        Exec(c,tx,"delete from studio_production_recipe_steps where recipe_id=$1",value);
        int order=0;
        foreach(var step in request.Steps.OrderBy(x=>x.SequenceOrder))
        {
            order++;Exec(c,tx,"""insert into studio_production_recipe_steps(id,recipe_id,task_template_id,sequence_order,is_mandatory,default_assignee,required_role,required_skill,due_offset_hours,depends_on_step_id)
                                 values($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)""",
              step.Id??Guid.NewGuid(),value,step.TaskTemplateId,step.SequenceOrder<=0?order:step.SequenceOrder,step.IsMandatory,(object?)Clean(step.DefaultAssignee)??DBNull.Value,(object?)Clean(step.RequiredRole)??DBNull.Value,(object?)Clean(step.RequiredSkill)??DBNull.Value,(object?)step.DueOffsetHours??DBNull.Value,(object?)step.DependsOnStepId??DBNull.Value);
        }
        tx.Commit();return GetRecipes().First(x=>x.Id==value);
    }

    public IReadOnlyList<JobActivity> GenerateTasks(Guid jobId,string actor,Guid? requestedRecipeId=null)
    {
        using var c=database.OpenConnection();
        Guid? jobTypeId=null,recipeId=requestedRecipeId;
        using(var q=new NpgsqlCommand("select job_type_id,coalesce(production_recipe_id,(select default_recipe_id from studio_job_types where id=job_type_id)) from studio_jobs where id=$1",c))
        {q.Parameters.AddWithValue(jobId);using var r=q.ExecuteReader();if(!r.Read())throw new KeyNotFoundException("Job was not found.");jobTypeId=r.IsDBNull(0)?null:r.GetGuid(0);if(recipeId is null&&!r.IsDBNull(1))recipeId=r.GetGuid(1);}
        if(recipeId is null)throw new InvalidOperationException("Assign a Job Type or Production Recipe before generating production tasks.");
        ProductionRecipeDefinition recipe=GetRecipes().FirstOrDefault(x=>x.Id==recipeId.Value)??throw new InvalidOperationException("Production Recipe was not found.");
        var directory=new StudioEmployeeDirectoryStore(database);
        var employees=directory.GetAll(true);
        var existing=new OperationalWorkStore(database).GetActivities(jobId).Where(x=>x.RecipeStepId.HasValue).Select(x=>x.RecipeStepId!.Value).ToHashSet();
        var store=new OperationalWorkStore(database);
        foreach(var step in recipe.Steps.Where(x=>x.Template.IsActive).OrderBy(x=>x.SequenceOrder))
        {
            if(existing.Contains(step.Id))continue;
            string? requiredRole=Clean(step.RequiredRole)??step.Template.RequiredRole;
            string? requiredSkill=Clean(step.RequiredSkill)??step.Template.RequiredSkill;
            string? preferred=Clean(step.DefaultAssignee)??step.Template.DefaultAssignee;
            string? assigned=Eligible(preferred,requiredRole,requiredSkill,employees)?preferred:null;
            string? warning=null;
            if(preferred is not null&&assigned is null)warning=$"Default assignee {preferred} is unavailable or ineligible.";
            if(assigned is null)
            {
                var candidates=employees.Where(e=>Eligible(e.UserName,requiredRole,requiredSkill,employees)).ToList();
                if(candidates.Count==1)assigned=candidates[0].UserName;
                else if(candidates.Count==0&&(requiredRole is not null||requiredSkill is not null))warning=$"No active assignable employee matches {(requiredRole is null?"":requiredRole)}{(requiredRole is not null&&requiredSkill is not null?" / ":"")}{(requiredSkill is null?"":requiredSkill)}.";
            }
            int? dueOffset=step.DueOffsetHours??step.Template.DueOffsetHours;
            DateTimeOffset? due=dueOffset.HasValue?DateTimeOffset.UtcNow.AddHours(dueOffset.Value):null;
            var request=new JobActivityRequest(step.Template.ActivityType,$"{step.Template.Name}",step.Template.Description,"NotStarted",step.Template.Priority,assigned,null,due,null,null,null,
                step.Template.ExpectedEffortMinutes,step.Template.Id,step.Id,step.SequenceOrder,step.Template.RequiresScheduling,warning);
            store.CreateActivity(jobId,request,actor);
        }
        using(var update=new NpgsqlCommand("update studio_jobs set production_recipe_id=$2 where id=$1",c)){update.Parameters.AddWithValue(jobId);update.Parameters.AddWithValue(recipeId.Value);update.ExecuteNonQuery();}
        return store.GetActivities(jobId);
    }

    public void AssignJobType(Guid jobId,Guid? jobTypeId,Guid? recipeId)
    {
        using var c=database.OpenConnection();using var cmd=new NpgsqlCommand("update studio_jobs set job_type_id=$2,production_recipe_id=$3 where id=$1",c);
        Add(cmd,jobId,jobTypeId,recipeId);if(cmd.ExecuteNonQuery()!=1)throw new KeyNotFoundException("Job was not found.");
    }

    public IReadOnlyList<EmployeeCapacitySummary> GetCapacity()
    {
        using var c=database.OpenConnection();using var cmd=new NpgsqlCommand("""
          select e.user_name,e.display_name,
            coalesce((select sum(a.expected_effort_minutes) from studio_job_activities a where lower(a.assigned_to)=lower(e.user_name) and a.status not in ('Completed','Cancelled')),0),
            coalesce((select count(*) from studio_jobs j where lower(j.job_lead)=lower(e.user_name) and j.status not in ('Completed','Closed','Cancelled','Published')),0),
            coalesce((select sum(extract(epoch from (se.ends_at_utc-se.starts_at_utc))/60)
              from studio_shoot_sessions ss join studio_schedule_entries se on se.id=ss.schedule_entry_id
              where (lower(ss.photographer_user_name)=lower(e.user_name) or lower(coalesce(ss.assistant_user_name,''))=lower(e.user_name))
                and se.status<>'Cancelled' and se.starts_at_utc>=now() and se.starts_at_utc<now()+interval '7 days'),0)
          from studio_employees e where e.is_active and e.is_assignable order by e.display_name
          """,c);
        using var r=cmd.ExecuteReader();var rows=new List<EmployeeCapacitySummary>();
        const int weekMinutes=40*60;
        while(r.Read())
        {
            int task=Convert.ToInt32(r.GetValue(2)),jobs=Convert.ToInt32(r.GetValue(3)),scheduled=Convert.ToInt32(r.GetValue(4));
            int assigned=task+scheduled;rows.Add(new(r.GetString(0),r.GetString(1),jobs,task,scheduled,assigned,Math.Min(999,(int)Math.Round(assigned*100d/weekMinutes))));
        }
        return rows;
    }

    private static bool Eligible(string? user,string? role,string? skill,IReadOnlyList<StudioEmployee> employees)
    {
        if(string.IsNullOrWhiteSpace(user))return false;
        var e=employees.FirstOrDefault(x=>string.Equals(x.UserName,user,StringComparison.OrdinalIgnoreCase));
        if(e is null||!e.IsActive||!e.IsAssignable)return false;
        if(role is not null&&!e.Roles.Any(x=>string.Equals(x,role,StringComparison.OrdinalIgnoreCase)))return false;
        if(skill is not null&&!e.Skills.Any(x=>string.Equals(x,skill,StringComparison.OrdinalIgnoreCase)))return false;
        return true;
    }

    private static void Add(NpgsqlCommand cmd,params object?[] values){foreach(var v in values)cmd.Parameters.AddWithValue(v??DBNull.Value);}
    private static void Exec(NpgsqlConnection c,NpgsqlTransaction tx,string sql,params object?[] values){using var cmd=new NpgsqlCommand(sql,c,tx);Add(cmd,values);cmd.ExecuteNonQuery();}
    private static string? Clean(string? value)=>string.IsNullOrWhiteSpace(value)?null:value.Trim();
    private static Guid Deterministic(string text){byte[] hash=System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(text));return new Guid(hash);}
}

public static class ProductionConfigurationEndpoints
{
    public static void MapProductionConfiguration(this WebApplication app)
    {
        var store=new ProductionConfigurationStore(app.Services.GetRequiredService<StudioDatabase>());store.EnsureSchema();
        app.MapGet("/api/production/job-types",(bool? active)=>Results.Ok(store.GetJobTypes(active==true))).RequireAuthorization();
        app.MapGet("/api/production/recipes",(bool? active)=>Results.Ok(store.GetRecipes(active==true))).RequireAuthorization();
        app.MapGet("/api/production/task-templates",(bool? active)=>Results.Ok(store.GetTemplates(active==true))).RequireAuthorization();
        app.MapGet("/api/production/capacity",()=>Results.Ok(store.GetCapacity())).RequireAuthorization();

        app.MapPut("/api/jobs/{jobId:guid}/production-configuration",(Guid jobId,JobProductionConfigurationRequest request,ClaimsPrincipal user,JobAccessService access)=>{
            if(!access.HasPermission(user,jobId,"ManageProduction"))return Results.Forbid();
            try{store.AssignJobType(jobId,request.JobTypeId,request.ProductionRecipeId);return Results.NoContent();}
            catch(KeyNotFoundException){return Results.NotFound();}
        }).RequireAuthorization();

        app.MapPost("/api/jobs/{jobId:guid}/production/generate-tasks",(Guid jobId,GenerateProductionTasksRequest request,ClaimsPrincipal user,JobAccessService access)=>{
            if(!access.HasPermission(user,jobId,"ManageProduction"))return Results.Forbid();
            try{return Results.Ok(store.GenerateTasks(jobId,UserName(user),request.RecipeId));}
            catch(Exception e){return Results.BadRequest(new{error=e.Message});}
        }).RequireAuthorization();

        var admin=app.MapGroup("/api/admin/production").RequireAuthorization(new AuthorizeAttribute{Roles=StudioRoleNames.Administrator});
        admin.MapGet("/job-types",()=>Results.Ok(store.GetJobTypes()));
        admin.MapPost("/job-types",(JobTypeRequest r)=>Results.Ok(store.SaveJobType(null,r)));
        admin.MapPut("/job-types/{id:guid}",(Guid id,JobTypeRequest r)=>Results.Ok(store.SaveJobType(id,r)));
        admin.MapGet("/task-templates",()=>Results.Ok(store.GetTemplates()));
        admin.MapPost("/task-templates",(TaskTemplateRequest r)=>Results.Ok(store.SaveTemplate(null,r)));
        admin.MapPut("/task-templates/{id:guid}",(Guid id,TaskTemplateRequest r)=>Results.Ok(store.SaveTemplate(id,r)));
        admin.MapGet("/recipes",()=>Results.Ok(store.GetRecipes()));
        admin.MapPost("/recipes",(ProductionRecipeRequest r)=>Results.Ok(store.SaveRecipe(null,r)));
        admin.MapPut("/recipes/{id:guid}",(Guid id,ProductionRecipeRequest r)=>Results.Ok(store.SaveRecipe(id,r)));
    }
    private static string UserName(ClaimsPrincipal user)=>user.FindFirstValue("login_name")??user.Identity?.Name??"Studio";
}

public sealed record JobTypeDefinition(Guid Id,string Name,string? Description,string? Category,Guid? DefaultRecipeId,bool IsActive,string? DefaultRecipeName);
public sealed record JobTypeRequest(string Name,string? Description,string? Category,Guid? DefaultRecipeId,bool IsActive=true);
public sealed record TaskTemplateDefinition(Guid Id,string Name,string? Description,string ActivityType,int ExpectedEffortMinutes,string? RequiredRole,string? RequiredSkill,string? DefaultAssignee,string Priority,bool RequiresScheduling,int? DueOffsetHours,bool IsMandatory,bool IsActive);
public sealed record TaskTemplateRequest(string Name,string? Description,string? ActivityType,int ExpectedEffortMinutes=60,string? RequiredRole=null,string? RequiredSkill=null,string? DefaultAssignee=null,string? Priority="Normal",bool RequiresScheduling=false,int? DueOffsetHours=null,bool IsMandatory=true,bool IsActive=true);
public sealed record ProductionRecipeStep(Guid Id,Guid RecipeId,Guid TaskTemplateId,int SequenceOrder,bool IsMandatory,string? DefaultAssignee,string? RequiredRole,string? RequiredSkill,int? DueOffsetHours,Guid? DependsOnStepId,TaskTemplateDefinition Template);
public sealed record ProductionRecipeDefinition(Guid Id,string Name,string? Description,bool IsActive,IReadOnlyList<ProductionRecipeStep> Steps);
public sealed record ProductionRecipeStepRequest(Guid? Id,Guid TaskTemplateId,int SequenceOrder,bool IsMandatory=true,string? DefaultAssignee=null,string? RequiredRole=null,string? RequiredSkill=null,int? DueOffsetHours=null,Guid? DependsOnStepId=null);
public sealed record ProductionRecipeRequest(string Name,string? Description,bool IsActive,IReadOnlyList<ProductionRecipeStepRequest> Steps);
public sealed record JobProductionConfigurationRequest(Guid? JobTypeId,Guid? ProductionRecipeId);
public sealed record GenerateProductionTasksRequest(Guid? RecipeId=null);
public sealed record EmployeeCapacitySummary(string UserName,string DisplayName,int JobsLed,int OpenTaskMinutes,int ScheduledMinutes,int AssignedMinutes,int UtilizationPercent);
