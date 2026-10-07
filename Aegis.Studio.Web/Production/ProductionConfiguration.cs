using System.Security.Claims;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Microsoft.AspNetCore.Authorization;
using Npgsql;

namespace Aegis.Studio.Web.Production;

public sealed class ProductionConfigurationStore
{
    private readonly StudioDatabase database;
    private readonly IConfiguration configuration;

    public ProductionConfigurationStore(StudioDatabase database, IConfiguration configuration)
    {
        this.database = database;
        this.configuration = configuration;
    }

    public void EnsureSchema()
    {
        using var c=database.OpenConnection();
        using var cmd=c.CreateCommand();
        cmd.CommandText="""
            create table if not exists studio_job_types(
              id uuid primary key,
              name text not null,
              description text null,
              is_active boolean not null default true,
              created_at_utc timestamptz not null default now(),
              updated_at_utc timestamptz not null default now()
            );
            create unique index if not exists ux_studio_job_types_name on studio_job_types(lower(name));

            create table if not exists studio_production_recipes(
              id uuid primary key,
              job_type_id uuid not null references studio_job_types(id) on delete cascade,
              name text not null,
              description text null,
              trigger_status text null,
              is_default boolean not null default false,
              is_active boolean not null default true,
              created_at_utc timestamptz not null default now(),
              updated_at_utc timestamptz not null default now()
            );
            create index if not exists ix_studio_production_recipes_type on studio_production_recipes(job_type_id,is_active,is_default);

            create table if not exists studio_task_templates(
              id uuid primary key,
              name text not null,
              description text null,
              activity_type text not null default 'Other',
              estimated_minutes integer not null default 60 check(estimated_minutes > 0),
              required_role text null,
              required_skills text[] not null default '{}',
              default_assignee text null,
              default_role text null,
              priority text not null default 'Normal',
              requires_scheduling boolean not null default false,
              due_offset_minutes integer null,
              is_active boolean not null default true,
              created_at_utc timestamptz not null default now(),
              updated_at_utc timestamptz not null default now()
            );
            create unique index if not exists ux_studio_task_templates_name on studio_task_templates(lower(name));

            create table if not exists studio_recipe_steps(
              id uuid primary key,
              recipe_id uuid not null references studio_production_recipes(id) on delete cascade,
              task_template_id uuid not null references studio_task_templates(id),
              sort_order integer not null default 0,
              is_mandatory boolean not null default true,
              assignee_override text null,
              role_override text null,
              due_offset_minutes_override integer null,
              created_at_utc timestamptz not null default now(),
              updated_at_utc timestamptz not null default now(),
              unique(recipe_id,task_template_id,sort_order)
            );
            create index if not exists ix_studio_recipe_steps_recipe on studio_recipe_steps(recipe_id,sort_order);

            alter table studio_jobs add column if not exists job_type_id uuid null references studio_job_types(id) on delete set null;
            create index if not exists ix_studio_jobs_job_type on studio_jobs(job_type_id,status);

            alter table studio_job_activities add column if not exists estimated_minutes integer null;
            alter table studio_job_activities add column if not exists task_template_id uuid null references studio_task_templates(id) on delete set null;
            alter table studio_job_activities add column if not exists recipe_step_id uuid null references studio_recipe_steps(id) on delete set null;
            alter table studio_job_activities add column if not exists assignment_issue text null;
            create unique index if not exists ux_studio_generated_activity_step on studio_job_activities(job_id,recipe_step_id) where recipe_step_id is not null;
            """;
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<StudioJobType> GetJobTypes(bool includeInactive=false)
    {
        using var c=database.OpenConnection();using var cmd=c.CreateCommand();
        cmd.CommandText="select id,name,description,is_active from studio_job_types where $1 or is_active order by name";
        cmd.Parameters.AddWithValue(includeInactive);
        using var r=cmd.ExecuteReader();List<StudioJobType> rows=[];
        while(r.Read())rows.Add(new(r.GetGuid(0),r.GetString(1),r.IsDBNull(2)?null:r.GetString(2),r.GetBoolean(3)));
        return rows;
    }

    public StudioJobType SaveJobType(Guid? id, SaveJobTypeRequest request)
    {
        if(string.IsNullOrWhiteSpace(request.Name))throw new ArgumentException("Job Type name is required.");
        Guid value=id??Guid.NewGuid();
        using var c=database.OpenConnection();using var cmd=c.CreateCommand();
        cmd.CommandText="""
            insert into studio_job_types(id,name,description,is_active,created_at_utc,updated_at_utc)
            values($1,$2,$3,$4,now(),now())
            on conflict(id) do update set name=excluded.name,description=excluded.description,is_active=excluded.is_active,updated_at_utc=now()
            """;
        cmd.Parameters.AddWithValue(value);cmd.Parameters.AddWithValue(request.Name.Trim());cmd.Parameters.AddWithValue((object?)Clean(request.Description)??DBNull.Value);cmd.Parameters.AddWithValue(request.IsActive);cmd.ExecuteNonQuery();
        return GetJobTypes(true).Single(x=>x.Id==value);
    }

    public IReadOnlyList<ProductionRecipe> GetRecipes(Guid? jobTypeId=null,bool includeInactive=false)
    {
        using var c=database.OpenConnection();using var cmd=c.CreateCommand();
        cmd.CommandText="""
          select r.id,r.job_type_id,r.name,r.description,r.trigger_status,r.is_default,r.is_active,t.name
          from studio_production_recipes r join studio_job_types t on t.id=r.job_type_id
          where ($1::uuid is null or r.job_type_id=$1) and ($2 or r.is_active)
          order by t.name,r.is_default desc,r.name
          """;
        cmd.Parameters.AddWithValue((object?)jobTypeId??DBNull.Value);cmd.Parameters.AddWithValue(includeInactive);
        using var r=cmd.ExecuteReader();List<ProductionRecipe> rows=[];
        while(r.Read())rows.Add(new(r.GetGuid(0),r.GetGuid(1),r.GetString(2),r.IsDBNull(3)?null:r.GetString(3),r.IsDBNull(4)?null:r.GetString(4),r.GetBoolean(5),r.GetBoolean(6),r.GetString(7)));
        return rows;
    }

    public ProductionRecipe SaveRecipe(Guid? id, SaveRecipeRequest request)
    {
        if(request.JobTypeId==Guid.Empty||string.IsNullOrWhiteSpace(request.Name))throw new ArgumentException("Recipe requires a Job Type and name.");
        Guid value=id??Guid.NewGuid();
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        if(request.IsDefault)
        {
            using var clear=new NpgsqlCommand("update studio_production_recipes set is_default=false,updated_at_utc=now() where job_type_id=$1 and id<>$2",c,tx);
            clear.Parameters.AddWithValue(request.JobTypeId);clear.Parameters.AddWithValue(value);clear.ExecuteNonQuery();
        }
        using(var cmd=new NpgsqlCommand("""
            insert into studio_production_recipes(id,job_type_id,name,description,trigger_status,is_default,is_active,created_at_utc,updated_at_utc)
            values($1,$2,$3,$4,$5,$6,$7,now(),now())
            on conflict(id) do update set job_type_id=excluded.job_type_id,name=excluded.name,description=excluded.description,trigger_status=excluded.trigger_status,is_default=excluded.is_default,is_active=excluded.is_active,updated_at_utc=now()
            """,c,tx))
        {
            object?[] values=[value,request.JobTypeId,request.Name.Trim(),(object?)Clean(request.Description)??DBNull.Value,(object?)Clean(request.TriggerStatus)??DBNull.Value,request.IsDefault,request.IsActive];
            foreach(var v in values)cmd.Parameters.AddWithValue(v??DBNull.Value);cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return GetRecipes(null,true).Single(x=>x.Id==value);
    }

    public IReadOnlyList<TaskTemplate> GetTemplates(bool includeInactive=false)
    {
        using var c=database.OpenConnection();using var cmd=c.CreateCommand();
        cmd.CommandText="""
          select id,name,description,activity_type,estimated_minutes,required_role,required_skills,default_assignee,default_role,priority,requires_scheduling,due_offset_minutes,is_active
          from studio_task_templates where $1 or is_active order by name
          """;
        cmd.Parameters.AddWithValue(includeInactive);
        using var r=cmd.ExecuteReader();List<TaskTemplate> rows=[];
        while(r.Read())rows.Add(new(r.GetGuid(0),r.GetString(1),r.IsDBNull(2)?null:r.GetString(2),r.GetString(3),r.GetInt32(4),r.IsDBNull(5)?null:r.GetString(5),r.GetFieldValue<string[]>(6),r.IsDBNull(7)?null:r.GetString(7),r.IsDBNull(8)?null:r.GetString(8),r.GetString(9),r.GetBoolean(10),r.IsDBNull(11)?null:r.GetInt32(11),r.GetBoolean(12)));
        return rows;
    }

    public TaskTemplate SaveTemplate(Guid? id, SaveTaskTemplateRequest request)
    {
        if(string.IsNullOrWhiteSpace(request.Name))throw new ArgumentException("Task Template name is required.");
        if(request.EstimatedMinutes<=0)throw new ArgumentException("Estimated effort must be greater than zero.");
        Guid value=id??Guid.NewGuid();
        using var c=database.OpenConnection();using var cmd=c.CreateCommand();
        cmd.CommandText="""
          insert into studio_task_templates(id,name,description,activity_type,estimated_minutes,required_role,required_skills,default_assignee,default_role,priority,requires_scheduling,due_offset_minutes,is_active,created_at_utc,updated_at_utc)
          values($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,now(),now())
          on conflict(id) do update set name=excluded.name,description=excluded.description,activity_type=excluded.activity_type,estimated_minutes=excluded.estimated_minutes,required_role=excluded.required_role,required_skills=excluded.required_skills,default_assignee=excluded.default_assignee,default_role=excluded.default_role,priority=excluded.priority,requires_scheduling=excluded.requires_scheduling,due_offset_minutes=excluded.due_offset_minutes,is_active=excluded.is_active,updated_at_utc=now()
          """;
        object?[] values=[value,request.Name.Trim(),(object?)Clean(request.Description)??DBNull.Value,Clean(request.ActivityType)??"Other",request.EstimatedMinutes,(object?)Clean(request.RequiredRole)??DBNull.Value,(request.RequiredSkills??[]).Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),(object?)Clean(request.DefaultAssignee)??DBNull.Value,(object?)Clean(request.DefaultRole)??DBNull.Value,Clean(request.Priority)??"Normal",request.RequiresScheduling,(object?)request.DueOffsetMinutes??DBNull.Value,request.IsActive];
        foreach(var v in values)cmd.Parameters.AddWithValue(v??DBNull.Value);cmd.ExecuteNonQuery();
        return GetTemplates(true).Single(x=>x.Id==value);
    }

    public IReadOnlyList<RecipeStep> GetSteps(Guid recipeId)
    {
        using var c=database.OpenConnection();using var cmd=c.CreateCommand();
        cmd.CommandText="""
          select s.id,s.recipe_id,s.task_template_id,s.sort_order,s.is_mandatory,s.assignee_override,s.role_override,s.due_offset_minutes_override,t.name
          from studio_recipe_steps s join studio_task_templates t on t.id=s.task_template_id
          where s.recipe_id=$1 order by s.sort_order,s.created_at_utc
          """;
        cmd.Parameters.AddWithValue(recipeId);
        using var r=cmd.ExecuteReader();List<RecipeStep> rows=[];
        while(r.Read())rows.Add(new(r.GetGuid(0),r.GetGuid(1),r.GetGuid(2),r.GetInt32(3),r.GetBoolean(4),r.IsDBNull(5)?null:r.GetString(5),r.IsDBNull(6)?null:r.GetString(6),r.IsDBNull(7)?null:r.GetInt32(7),r.GetString(8)));
        return rows;
    }

    public RecipeStep SaveStep(Guid recipeId,Guid? id,SaveRecipeStepRequest request)
    {
        if(request.TaskTemplateId==Guid.Empty)throw new ArgumentException("Recipe step requires a Task Template.");
        Guid value=id??Guid.NewGuid();
        using var c=database.OpenConnection();using var cmd=c.CreateCommand();
        cmd.CommandText="""
          insert into studio_recipe_steps(id,recipe_id,task_template_id,sort_order,is_mandatory,assignee_override,role_override,due_offset_minutes_override,created_at_utc,updated_at_utc)
          values($1,$2,$3,$4,$5,$6,$7,$8,now(),now())
          on conflict(id) do update set task_template_id=excluded.task_template_id,sort_order=excluded.sort_order,is_mandatory=excluded.is_mandatory,assignee_override=excluded.assignee_override,role_override=excluded.role_override,due_offset_minutes_override=excluded.due_offset_minutes_override,updated_at_utc=now()
          """;
        object?[] values=[value,recipeId,request.TaskTemplateId,request.SortOrder,request.IsMandatory,(object?)Clean(request.AssigneeOverride)??DBNull.Value,(object?)Clean(request.RoleOverride)??DBNull.Value,(object?)request.DueOffsetMinutesOverride??DBNull.Value];
        foreach(var v in values)cmd.Parameters.AddWithValue(v??DBNull.Value);cmd.ExecuteNonQuery();
        return GetSteps(recipeId).Single(x=>x.Id==value);
    }

    public void SetJobType(Guid jobId,Guid? jobTypeId)
    {
        using var c=database.OpenConnection();using var cmd=c.CreateCommand();
        cmd.CommandText="update studio_jobs set job_type_id=$2 where id=$1";
        cmd.Parameters.AddWithValue(jobId);cmd.Parameters.AddWithValue((object?)jobTypeId??DBNull.Value);
        if(cmd.ExecuteNonQuery()!=1)throw new KeyNotFoundException("Job was not found.");
    }

    public JobProductionConfiguration GetJobConfiguration(Guid jobId)
    {
        using var c=database.OpenConnection();using var cmd=c.CreateCommand();
        cmd.CommandText="""
          select j.id,j.job_type_id,t.name,r.id,r.name
          from studio_jobs j
          left join studio_job_types t on t.id=j.job_type_id
          left join lateral(select id,name from studio_production_recipes where job_type_id=j.job_type_id and is_active order by is_default desc,created_at_utc limit 1) r on true
          where j.id=$1
          """;
        cmd.Parameters.AddWithValue(jobId);using var r=cmd.ExecuteReader();
        if(!r.Read())throw new KeyNotFoundException("Job was not found.");
        return new(r.GetGuid(0),r.IsDBNull(1)?null:r.GetGuid(1),r.IsDBNull(2)?null:r.GetString(2),r.IsDBNull(3)?null:r.GetGuid(3),r.IsDBNull(4)?null:r.GetString(4));
    }

    public IReadOnlyList<GeneratedProductionTask> GenerateForJob(Guid jobId,string actor,Guid? recipeId=null)
    {
        using var c=database.OpenConnection();
        Guid selectedRecipe;
        string jobTitle;
        using(var find=new NpgsqlCommand("""
          select j.title,coalesce($2::uuid,(select r.id from studio_production_recipes r where r.job_type_id=j.job_type_id and r.is_active order by r.is_default desc,r.created_at_utc limit 1))
          from studio_jobs j where j.id=$1
          """,c))
        {
            find.Parameters.AddWithValue(jobId);find.Parameters.AddWithValue((object?)recipeId??DBNull.Value);
            using var r=find.ExecuteReader();
            if(!r.Read())throw new KeyNotFoundException("Job was not found.");
            jobTitle=r.GetString(0);
            if(r.IsDBNull(1))throw new InvalidOperationException("Assign a Job Type with an active Production Recipe before generating production work.");
            selectedRecipe=r.GetGuid(1);
        }

        using var stepCmd=new NpgsqlCommand("""
          select s.id,t.id,t.name,t.description,t.activity_type,t.estimated_minutes,t.required_role,t.required_skills,t.default_assignee,t.default_role,t.priority,t.requires_scheduling,
                 coalesce(s.due_offset_minutes_override,t.due_offset_minutes),s.assignee_override,s.role_override,s.is_mandatory
          from studio_recipe_steps s join studio_task_templates t on t.id=s.task_template_id
          where s.recipe_id=$1 and t.is_active order by s.sort_order,s.created_at_utc
          """,c);
        stepCmd.Parameters.AddWithValue(selectedRecipe);
        using var sr=stepCmd.ExecuteReader();List<GenerationStep> steps=[];
        while(sr.Read())steps.Add(new(sr.GetGuid(0),sr.GetGuid(1),sr.GetString(2),sr.IsDBNull(3)?null:sr.GetString(3),sr.GetString(4),sr.GetInt32(5),sr.IsDBNull(6)?null:sr.GetString(6),sr.GetFieldValue<string[]>(7),sr.IsDBNull(8)?null:sr.GetString(8),sr.IsDBNull(9)?null:sr.GetString(9),sr.GetString(10),sr.GetBoolean(11),sr.IsDBNull(12)?null:sr.GetInt32(12),sr.IsDBNull(13)?null:sr.GetString(13),sr.IsDBNull(14)?null:sr.GetString(14),sr.GetBoolean(15)));
        sr.Close();
        List<GeneratedProductionTask> result=[];
        foreach(var step in steps)
        {
            AssignmentDecision assignment=ResolveAssignment(c,step);
            Guid activityId=Guid.NewGuid();
            DateTimeOffset? due=step.DueOffsetMinutes.HasValue?DateTimeOffset.UtcNow.AddMinutes(step.DueOffsetMinutes.Value):null;
            using var insert=new NpgsqlCommand("""
              insert into studio_job_activities(id,job_id,activity_type,title,description,status,priority,assigned_to,due_at_utc,created_by,created_at_utc,updated_at_utc,estimated_minutes,task_template_id,recipe_step_id,assignment_issue)
              values($1,$2,$3,$4,$5,'NotStarted',$6,$7,$8,$9,now(),now(),$10,$11,$12,$13)
              on conflict(job_id,recipe_step_id) where recipe_step_id is not null do nothing
              returning id
              """,c);
            object?[] values=[activityId,jobId,step.ActivityType,$"{step.Name} — {jobTitle}",(object?)step.Description??DBNull.Value,step.Priority,(object?)assignment.UserName??DBNull.Value,(object?)due??DBNull.Value,actor,step.EstimatedMinutes,step.TemplateId,step.StepId,(object?)assignment.Issue??DBNull.Value];
            foreach(var v in values)insert.Parameters.AddWithValue(v??DBNull.Value);
            object? created=insert.ExecuteScalar();
            if(created is Guid createdId)
            {
                result.Add(new(createdId,step.StepId,step.TemplateId,step.Name,assignment.UserName,assignment.Issue,step.EstimatedMinutes,due,step.RequiresScheduling));
            }
        }
        return result;
    }

    public IReadOnlyList<GeneratedProductionTask> GenerateForJobIfTriggered(Guid jobId,string status,string actor)
    {
        using var c=database.OpenConnection();using var cmd=new NpgsqlCommand("""
          select r.id from studio_jobs j join studio_production_recipes r on r.job_type_id=j.job_type_id
          where j.id=$1 and r.is_active and r.trigger_status is not null and lower(r.trigger_status)=lower($2)
          order by r.is_default desc,r.created_at_utc limit 1
          """,c);
        cmd.Parameters.AddWithValue(jobId);cmd.Parameters.AddWithValue(status);
        object? recipe=cmd.ExecuteScalar();
        return recipe is Guid id?GenerateForJob(jobId,actor,id):[];
    }

    public IReadOnlyList<EmployeeCapacity> GetCapacity(DateTimeOffset from,DateTimeOffset to)
    {
        if(to<=from)throw new ArgumentException("Capacity end must be after start.");
        double days=Math.Max(1,(to.ToUniversalTime()-from.ToUniversalTime()).TotalDays);
        double baseMinutes=days*Math.Clamp(configuration.GetValue<double?>("Scheduling:DefaultDailyCapacityHours")??8d,1d,24d)*60d;
        using var c=database.OpenConnection();using var cmd=new NpgsqlCommand("""
          select e.user_name,e.display_name,
            coalesce((select sum(coalesce(a.estimated_minutes,60)) from studio_job_activities a
              where lower(a.assigned_to)=lower(e.user_name) and a.status not in ('Completed','Cancelled')
                and (a.due_at_utc is null or a.due_at_utc<$2)),0) task_minutes,
            coalesce((select sum(extract(epoch from (least(s.planned_end_utc,$2)-greatest(s.call_time_utc,$1)))/60.0)
              from studio_shoot_sessions s where s.status<>'Cancelled'
                and (lower(s.photographer_user_name)=lower(e.user_name) or lower(coalesce(s.assistant_user_name,''))=lower(e.user_name))
                and s.call_time_utc<$2 and s.planned_end_utc>$1),0) scheduled_minutes,
            coalesce((select count(*) from studio_jobs j where lower(j.job_lead)=lower(e.user_name) and j.status not in ('Completed','Closed','Cancelled','Published')),0) jobs_led
          from studio_employees e where e.is_active and e.is_assignable order by e.display_name
          """,c);
        cmd.Parameters.AddWithValue(from.ToUniversalTime());cmd.Parameters.AddWithValue(to.ToUniversalTime());
        using var r=cmd.ExecuteReader();List<EmployeeCapacity> rows=[];
        while(r.Read())
        {
            double task=Convert.ToDouble(r.GetValue(2));double scheduled=Convert.ToDouble(r.GetValue(3));int led=Convert.ToInt32(r.GetValue(4));
            double assigned=task+scheduled;double utilization=baseMinutes<=0?0:assigned/baseMinutes*100d;
            string state=utilization>100?"OverCapacity":utilization>=85?"NearCapacity":"Available";
            rows.Add(new(r.GetString(0),r.GetString(1),Math.Round(task,1),Math.Round(scheduled,1),Math.Round(assigned,1),Math.Round(baseMinutes,1),Math.Round(utilization,1),led,state));
        }
        return rows;
    }

    private AssignmentDecision ResolveAssignment(NpgsqlConnection c,GenerationStep step)
    {
        string? requested=Clean(step.AssigneeOverride)??Clean(step.DefaultAssignee);
        string? role=Clean(step.RoleOverride)??Clean(step.RequiredRole)??Clean(step.DefaultRole);
        string[] skills=step.RequiredSkills??[];

        bool Eligible(string user)
        {
            using var q=new NpgsqlCommand("""
              select exists(
                select 1 from studio_employees e
                where lower(e.user_name)=lower($1) and e.is_active and e.is_assignable
                  and ($2::text is null or exists(
                    select 1 from studio_global_operational_role_members m join studio_global_operational_roles r on r.id=m.role_id
                    where m.is_active and r.is_active and lower(m.user_name)=lower(e.user_name) and lower(r.name)=lower($2)))
                  and ($3::text[] <@ e.skills)
              )
              """,c);
            q.Parameters.AddWithValue(user);q.Parameters.AddWithValue((object?)role??DBNull.Value);q.Parameters.AddWithValue(skills);
            return (bool)q.ExecuteScalar()!;
        }
        bool CapacityAllows(string user)
        {
            double weeklyCapacity=Math.Clamp(configuration.GetValue<double?>("Scheduling:DefaultDailyCapacityHours")??8d,1d,24d)*5d*60d;
            using var q=new NpgsqlCommand("""
              select
                coalesce((select sum(coalesce(a.estimated_minutes,60)) from studio_job_activities a
                  where lower(a.assigned_to)=lower($1) and a.status not in ('Completed','Cancelled')),0)
                + coalesce((select sum(extract(epoch from (s.planned_end_utc-s.call_time_utc))/60.0)
                  from studio_shoot_sessions s where s.status<>'Cancelled'
                    and (lower(s.photographer_user_name)=lower($1) or lower(coalesce(s.assistant_user_name,''))=lower($1))
                    and s.call_time_utc<now()+interval '7 days' and s.planned_end_utc>now()),0)
              """,c);
            q.Parameters.AddWithValue(user);
            double assigned=Convert.ToDouble(q.ExecuteScalar());
            return assigned+step.EstimatedMinutes<=weeklyCapacity;
        }

        if(requested is not null)
        {
            if(!Eligible(requested))return new(null,$"Default assignee {requested} is inactive, unassignable, or does not meet role/skill requirements.");
            return CapacityAllows(requested)?new(requested,null):new(null,$"Default assignee {requested} is at or over configured capacity; assignment requires review.");
        }

        using var candidates=new NpgsqlCommand("""
          select e.user_name
          from studio_employees e
          where e.is_active and e.is_assignable
            and ($1::text is null or exists(
              select 1 from studio_global_operational_role_members m join studio_global_operational_roles r on r.id=m.role_id
              where m.is_active and r.is_active and lower(m.user_name)=lower(e.user_name) and lower(r.name)=lower($1)))
            and ($2::text[] <@ e.skills)
          order by coalesce((select sum(coalesce(a.estimated_minutes,60)) from studio_job_activities a where lower(a.assigned_to)=lower(e.user_name) and a.status not in ('Completed','Cancelled')),0),e.display_name
          limit 20
          """,c);
        candidates.Parameters.AddWithValue((object?)role??DBNull.Value);candidates.Parameters.AddWithValue(skills);
        using var reader=candidates.ExecuteReader();
        List<string> eligible=[];
        while(reader.Read())eligible.Add(reader.GetString(0));
        reader.Close();
        string? selected=eligible.FirstOrDefault(CapacityAllows);
        if(selected is not null)return new(selected,null);
        return eligible.Count==0
            ? new(null,"No active assignable employee meets the required role/skill criteria.")
            : new(null,"Eligible employees are at or over configured capacity; assignment requires review.");
    }

    private static string? Clean(string? value)=>string.IsNullOrWhiteSpace(value)?null:value.Trim();
    private sealed record GenerationStep(Guid StepId,Guid TemplateId,string Name,string? Description,string ActivityType,int EstimatedMinutes,string? RequiredRole,string[] RequiredSkills,string? DefaultAssignee,string? DefaultRole,string Priority,bool RequiresScheduling,int? DueOffsetMinutes,string? AssigneeOverride,string? RoleOverride,bool Mandatory);
    private sealed record AssignmentDecision(string? UserName,string? Issue);
}

public static class ProductionConfigurationEndpoints
{
    public static void MapProductionConfiguration(this WebApplication app)
    {
        var database=app.Services.GetRequiredService<StudioDatabase>();
        var configuration=app.Services.GetRequiredService<IConfiguration>();
        var store=new ProductionConfigurationStore(database,configuration);
        store.EnsureSchema();

        app.MapGet("/api/production-config/job-types",()=>Results.Ok(store.GetJobTypes())).RequireAuthorization();
        app.MapGet("/api/production-config/recipes",(Guid? jobTypeId)=>Results.Ok(store.GetRecipes(jobTypeId))).RequireAuthorization();
        app.MapGet("/api/production-config/templates",()=>Results.Ok(store.GetTemplates())).RequireAuthorization();
        app.MapGet("/api/production-config/recipes/{recipeId:guid}/steps",(Guid recipeId)=>Results.Ok(store.GetSteps(recipeId))).RequireAuthorization();
        app.MapGet("/api/production/capacity",(DateTimeOffset from,DateTimeOffset to)=>Results.Ok(store.GetCapacity(from,to))).RequireAuthorization();

        RouteGroupBuilder admin=app.MapGroup("/api/admin/production-config").RequireAuthorization(new AuthorizeAttribute{Roles=StudioRoleNames.Administrator});
        admin.MapPost("/job-types",(SaveJobTypeRequest request)=>Results.Ok(store.SaveJobType(null,request)));
        admin.MapPut("/job-types/{id:guid}",(Guid id,SaveJobTypeRequest request)=>Results.Ok(store.SaveJobType(id,request)));
        admin.MapPost("/recipes",(SaveRecipeRequest request)=>Results.Ok(store.SaveRecipe(null,request)));
        admin.MapPut("/recipes/{id:guid}",(Guid id,SaveRecipeRequest request)=>Results.Ok(store.SaveRecipe(id,request)));
        admin.MapPost("/templates",(SaveTaskTemplateRequest request)=>Results.Ok(store.SaveTemplate(null,request)));
        admin.MapPut("/templates/{id:guid}",(Guid id,SaveTaskTemplateRequest request)=>Results.Ok(store.SaveTemplate(id,request)));
        admin.MapPost("/recipes/{recipeId:guid}/steps",(Guid recipeId,SaveRecipeStepRequest request)=>Results.Ok(store.SaveStep(recipeId,null,request)));
        admin.MapPut("/recipes/{recipeId:guid}/steps/{id:guid}",(Guid recipeId,Guid id,SaveRecipeStepRequest request)=>Results.Ok(store.SaveStep(recipeId,id,request)));

        app.MapGet("/api/jobs/{jobId:guid}/production-config",(Guid jobId,ClaimsPrincipal user,JobAccessService access)=>
            access.HasPermission(user,jobId,"ViewJob")?Results.Ok(store.GetJobConfiguration(jobId)):Results.Forbid()).RequireAuthorization();

        app.MapPut("/api/jobs/{jobId:guid}/job-type",(Guid jobId,SetJobTypeRequest request,ClaimsPrincipal user,JobAccessService access)=>
        {
            if(!access.HasPermission(user,jobId,"EditJob"))return Results.Forbid();
            store.SetJobType(jobId,request.JobTypeId);return Results.Ok(store.GetJobConfiguration(jobId));
        }).RequireAuthorization();

        app.MapPost("/api/jobs/{jobId:guid}/production/generate",(Guid jobId,GenerateProductionRequest request,ClaimsPrincipal user,JobAccessService access)=>
        {
            if(!access.HasPermission(user,jobId,"ManageProduction"))return Results.Forbid();
            string actor=user.FindFirst("login_name")?.Value??user.Identity?.Name??"Studio";
            return Results.Ok(store.GenerateForJob(jobId,actor,request.RecipeId));
        }).RequireAuthorization();
    }
}

public sealed record StudioJobType(Guid Id,string Name,string? Description,bool IsActive);
public sealed record SaveJobTypeRequest(string Name,string? Description,bool IsActive=true);
public sealed record ProductionRecipe(Guid Id,Guid JobTypeId,string Name,string? Description,string? TriggerStatus,bool IsDefault,bool IsActive,string JobTypeName);
public sealed record SaveRecipeRequest(Guid JobTypeId,string Name,string? Description,string? TriggerStatus,bool IsDefault=true,bool IsActive=true);
public sealed record TaskTemplate(Guid Id,string Name,string? Description,string ActivityType,int EstimatedMinutes,string? RequiredRole,IReadOnlyList<string> RequiredSkills,string? DefaultAssignee,string? DefaultRole,string Priority,bool RequiresScheduling,int? DueOffsetMinutes,bool IsActive);
public sealed record SaveTaskTemplateRequest(string Name,string? Description,string? ActivityType,int EstimatedMinutes=60,string? RequiredRole=null,IReadOnlyList<string>? RequiredSkills=null,string? DefaultAssignee=null,string? DefaultRole=null,string? Priority="Normal",bool RequiresScheduling=false,int? DueOffsetMinutes=null,bool IsActive=true);
public sealed record RecipeStep(Guid Id,Guid RecipeId,Guid TaskTemplateId,int SortOrder,bool IsMandatory,string? AssigneeOverride,string? RoleOverride,int? DueOffsetMinutesOverride,string TemplateName);
public sealed record SaveRecipeStepRequest(Guid TaskTemplateId,int SortOrder=0,bool IsMandatory=true,string? AssigneeOverride=null,string? RoleOverride=null,int? DueOffsetMinutesOverride=null);
public sealed record SetJobTypeRequest(Guid? JobTypeId);
public sealed record GenerateProductionRequest(Guid? RecipeId=null);
public sealed record JobProductionConfiguration(Guid JobId,Guid? JobTypeId,string? JobTypeName,Guid? RecipeId,string? RecipeName);
public sealed record GeneratedProductionTask(Guid ActivityId,Guid RecipeStepId,Guid TemplateId,string TemplateName,string? AssignedTo,string? AssignmentIssue,int EstimatedMinutes,DateTimeOffset? DueAtUtc,bool RequiresScheduling);
public sealed record EmployeeCapacity(string UserName,string DisplayName,double TaskMinutes,double ScheduledMinutes,double AssignedMinutes,double CapacityMinutes,double UtilizationPercent,int JobsLed,string State);
