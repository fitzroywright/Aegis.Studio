using System.Security.Claims;
using Aegis.Studio.Web.Persistence;
using Microsoft.AspNetCore.Authorization;
using Npgsql;

namespace Aegis.Studio.Web.Operations;

public sealed class StudioEmployeeDirectoryStore
{
    private readonly StudioDatabase database;
    public StudioEmployeeDirectoryStore(StudioDatabase database){this.database=database;}

    public void EnsureSchema()
    {
        using var c=database.OpenConnection();using var cmd=c.CreateCommand();
        cmd.CommandText="""
            create table if not exists studio_employees(
              user_name text primary key,
              display_name text not null,
              email_address text null,
              is_active boolean not null default true,
              is_assignable boolean not null default true,
              source text not null default 'Manual',
              external_id text null,
              skills text[] not null default '{}',
              workday_user_name text null,
              source_active boolean null,
              sync_state text not null default 'Local',
              last_synced_at_utc timestamptz null,
              created_at_utc timestamptz not null default now(),
              updated_at_utc timestamptz not null default now()
            );
            alter table studio_employees add column if not exists source_active boolean null;
            alter table studio_employees add column if not exists sync_state text not null default 'Local';
            create index if not exists ix_studio_employees_assignable on studio_employees(is_active,is_assignable,display_name);
            create table if not exists studio_employee_sync_runs(
              id uuid primary key,provider text not null,observed_count integer not null,missing_count integer not null,
              completed_at_utc timestamptz not null,detail text null
            );
            create index if not exists ix_studio_employee_sync_runs_provider on studio_employee_sync_runs(provider,completed_at_utc desc);
            """;
        cmd.ExecuteNonQuery();
        RefreshFromStudioSources();
    }

    private void RefreshFromStudioSources()
    {
        using var c=database.OpenConnection();using var cmd=c.CreateCommand();
        cmd.CommandText="""
            insert into studio_employees(user_name,display_name,is_active,is_assignable,source,last_synced_at_utc)
              select user_name,max(display_name),bool_or(is_active),true,'Existing Studio identity',now() from studio_global_operational_role_members group by user_name
              on conflict(user_name) do update set display_name=excluded.display_name,updated_at_utc=now();
            insert into studio_employees(user_name,display_name,is_active,is_assignable,source,workday_user_name,last_synced_at_utc)
              select user_name,display_name,is_active,is_active,'Studio photographer',user_name,now() from studio_photographers
              on conflict(user_name) do update set display_name=excluded.display_name,workday_user_name=coalesce(studio_employees.workday_user_name,excluded.workday_user_name),updated_at_utc=now();
            insert into studio_employees(user_name,display_name,is_active,is_assignable,source,last_synced_at_utc)
              select user_name,max(display_name),bool_or(is_active),true,'Workspace member',now() from studio_workspace_members group by user_name
              on conflict(user_name) do update set display_name=excluded.display_name,updated_at_utc=now();
            """;
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<StudioEmployee> GetAll(bool assignableOnly=false)
    {
        RefreshFromStudioSources();
        using var c=database.OpenConnection();using var cmd=c.CreateCommand();
        cmd.CommandText="""
            select e.user_name,e.display_name,e.email_address,e.is_active,e.is_assignable,e.source,e.external_id,e.skills,e.workday_user_name,e.last_synced_at_utc,
              coalesce(array(select distinct r.name from studio_global_operational_role_members m join studio_global_operational_roles r on r.id=m.role_id
                where m.is_active and r.is_active and lower(m.user_name)=lower(e.user_name) order by r.name),'{}'),
              e.source_active,e.sync_state
            from studio_employees e where not $1 or (e.is_active and e.is_assignable) order by e.display_name,e.user_name
            """;
        cmd.Parameters.AddWithValue(assignableOnly);
        using var r=cmd.ExecuteReader();List<StudioEmployee> rows=[];
        while(r.Read())rows.Add(new(r.GetString(0),r.GetString(1),r.IsDBNull(2)?null:r.GetString(2),r.GetBoolean(3),r.GetBoolean(4),r.GetString(5),r.IsDBNull(6)?null:r.GetString(6),r.GetFieldValue<string[]>(7),r.IsDBNull(8)?null:r.GetString(8),r.IsDBNull(9)?null:r.GetFieldValue<DateTimeOffset>(9),r.GetFieldValue<string[]>(10),r.IsDBNull(11)?null:r.GetBoolean(11),r.GetString(12)));
        return rows;
    }

    public EmployeeSyncResult SyncProvider(string provider,IReadOnlyList<StudioEmployeeSyncItem> employees)
    {
        string source=Clean(provider)??throw new ArgumentException("Provider name is required.");
        DateTimeOffset now=DateTimeOffset.UtcNow;
        string[] observed=(employees??[]).Where(x=>!string.IsNullOrWhiteSpace(x.UserName)).Select(x=>x.UserName.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        foreach(var item in employees??[])
        {
            string? cleanedUser=Clean(item.UserName);if(cleanedUser is null)continue;
            string user=cleanedUser;
            string display=Clean(item.DisplayName)??user;
            using var cmd=new NpgsqlCommand("""
              insert into studio_employees(user_name,display_name,email_address,is_active,is_assignable,source,external_id,skills,workday_user_name,source_active,sync_state,last_synced_at_utc,created_at_utc,updated_at_utc)
              values($1,$2,$3,true,true,$4,$5,'{}',$6,$7,'Present',$8,now(),now())
              on conflict(user_name) do update set display_name=excluded.display_name,email_address=coalesce(excluded.email_address,studio_employees.email_address),
                source=excluded.source,external_id=excluded.external_id,workday_user_name=coalesce(studio_employees.workday_user_name,excluded.workday_user_name),
                source_active=excluded.source_active,sync_state='Present',last_synced_at_utc=excluded.last_synced_at_utc,updated_at_utc=now()
              """,c,tx);
            object?[] values=[user,display,(object?)Clean(item.EmailAddress)??DBNull.Value,source,(object?)Clean(item.ExternalId)??DBNull.Value,(object?)Clean(item.WorkDayUserName)??DBNull.Value,item.SourceActive,now];
            foreach(var value in values)cmd.Parameters.AddWithValue(value??DBNull.Value);cmd.ExecuteNonQuery();
        }
        using(var missing=new NpgsqlCommand("""
          update studio_employees set source_active=false,sync_state='Missing',last_synced_at_utc=$3,updated_at_utc=now()
          where lower(source)=lower($1) and not(user_name=any($2))
          """,c,tx))
        {
            missing.Parameters.AddWithValue(source);missing.Parameters.AddWithValue(observed);missing.Parameters.AddWithValue(now);
            int missingCount=missing.ExecuteNonQuery();
            using var audit=new NpgsqlCommand("insert into studio_employee_sync_runs(id,provider,observed_count,missing_count,completed_at_utc,detail) values($1,$2,$3,$4,$5,$6)",c,tx);
            audit.Parameters.AddWithValue(Guid.NewGuid());audit.Parameters.AddWithValue(source);audit.Parameters.AddWithValue(observed.Length);audit.Parameters.AddWithValue(missingCount);audit.Parameters.AddWithValue(now);
            audit.Parameters.AddWithValue("Provider snapshot reconciled without deleting employees or overwriting Studio active/assignable state.");audit.ExecuteNonQuery();
            tx.Commit();
            return new(source,observed.Length,missingCount,now);
        }
    }

    public bool IsAssignable(string userName)=>GetAll(true).Any(x=>string.Equals(x.UserName,userName,StringComparison.OrdinalIgnoreCase));

    public StudioEmployee Save(StudioEmployeeRequest request)
    {
        string user=request.UserName?.Trim()??"";if(user.Length==0)throw new ArgumentException("User name is required.");
        string display=string.IsNullOrWhiteSpace(request.DisplayName)?user:request.DisplayName.Trim();
        using var c=database.OpenConnection();using var cmd=c.CreateCommand();
        cmd.CommandText="""
          insert into studio_employees(user_name,display_name,email_address,is_active,is_assignable,source,external_id,skills,workday_user_name,last_synced_at_utc,created_at_utc,updated_at_utc)
          values($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,now(),now())
          on conflict(user_name) do update set display_name=excluded.display_name,email_address=excluded.email_address,is_active=excluded.is_active,
            is_assignable=excluded.is_assignable,source=excluded.source,external_id=excluded.external_id,skills=excluded.skills,
            workday_user_name=excluded.workday_user_name,last_synced_at_utc=excluded.last_synced_at_utc,updated_at_utc=now()
          """;
        object?[] values=[user,display,(object?)Clean(request.EmailAddress)??DBNull.Value,request.IsActive,request.IsAssignable,Clean(request.Source)??"Manual",
          (object?)Clean(request.ExternalId)??DBNull.Value,request.Skills?.Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()??[],
          (object?)Clean(request.WorkDayUserName)??DBNull.Value,(object?)request.LastSyncedAtUtc??DBNull.Value];
        foreach(var v in values)cmd.Parameters.AddWithValue(v??DBNull.Value);cmd.ExecuteNonQuery();
        return GetAll().First(x=>string.Equals(x.UserName,user,StringComparison.OrdinalIgnoreCase));
    }
    private static string? Clean(string? value)=>string.IsNullOrWhiteSpace(value)?null:value.Trim();
}

public static class StudioEmployeeDirectoryEndpoints
{
    public static void MapStudioEmployeeDirectory(this WebApplication app)
    {
        StudioEmployeeDirectoryStore store=new(app.Services.GetRequiredService<StudioDatabase>());store.EnsureSchema();
        app.MapGet("/api/studio-employees",(bool? assignable)=>Results.Ok(store.GetAll(assignable==true))).RequireAuthorization();
        RouteGroupBuilder admin=app.MapGroup("/api/admin/studio-employees").RequireAuthorization(new AuthorizeAttribute{Roles=StudioRoleNames.Administrator});
        admin.MapGet("/",()=>Results.Ok(store.GetAll()));
        admin.MapPost("/",(StudioEmployeeRequest request)=>Results.Ok(store.Save(request)));
        admin.MapPut("/{userName}",(string userName,StudioEmployeeRequest request)=>Results.Ok(store.Save(request with{UserName=userName})));
        admin.MapPost("/sync/{provider}",(string provider,StudioEmployeeSyncRequest request)=>Results.Ok(store.SyncProvider(provider,request.Employees??[])));
    }
}
public sealed record StudioEmployee(string UserName,string DisplayName,string? EmailAddress,bool IsActive,bool IsAssignable,string Source,string? ExternalId,IReadOnlyList<string> Skills,string? WorkDayUserName,DateTimeOffset? LastSyncedAtUtc,IReadOnlyList<string> Roles,bool? SourceActive=null,string SyncState="Local");
public sealed record StudioEmployeeRequest(string UserName,string? DisplayName,string? EmailAddress,bool IsActive=true,bool IsAssignable=true,string? Source="Manual",string? ExternalId=null,IReadOnlyList<string>? Skills=null,string? WorkDayUserName=null,DateTimeOffset? LastSyncedAtUtc=null);
public sealed record StudioEmployeeSyncItem(string UserName,string? DisplayName,string? EmailAddress,string? ExternalId=null,bool SourceActive=true,string? WorkDayUserName=null);
public sealed record StudioEmployeeSyncRequest(IReadOnlyList<StudioEmployeeSyncItem>? Employees);
public sealed record EmployeeSyncResult(string Provider,int ObservedCount,int MissingCount,DateTimeOffset CompletedAtUtc);
