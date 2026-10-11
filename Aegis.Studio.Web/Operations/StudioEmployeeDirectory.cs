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
              last_synced_at_utc timestamptz null,
              created_at_utc timestamptz not null default now(),
              updated_at_utc timestamptz not null default now()
            );
            create index if not exists ix_studio_employees_assignable on studio_employees(is_active,is_assignable,display_name);
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
              on conflict(user_name) do nothing;
            insert into studio_employees(user_name,display_name,is_active,is_assignable,source,workday_user_name,last_synced_at_utc)
              select user_name,display_name,is_active,is_active,'Studio photographer',user_name,now() from studio_photographers
              on conflict(user_name) do nothing;
            insert into studio_employees(user_name,display_name,is_active,is_assignable,source,last_synced_at_utc)
              select user_name,max(display_name),bool_or(is_active),true,'Workspace member',now() from studio_workspace_members group by user_name
              on conflict(user_name) do nothing;
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
                where m.is_active and r.is_active and lower(m.user_name)=lower(e.user_name) order by r.name),'{}')
            from studio_employees e where not $1 or (e.is_active and e.is_assignable) order by e.display_name,e.user_name
            """;
        cmd.Parameters.AddWithValue(assignableOnly);
        using var r=cmd.ExecuteReader();List<StudioEmployee> rows=[];
        while(r.Read())rows.Add(new(r.GetString(0),r.GetString(1),r.IsDBNull(2)?null:r.GetString(2),r.GetBoolean(3),r.GetBoolean(4),r.GetString(5),r.IsDBNull(6)?null:r.GetString(6),r.GetFieldValue<string[]>(7),r.IsDBNull(8)?null:r.GetString(8),r.IsDBNull(9)?null:r.GetFieldValue<DateTimeOffset>(9),r.GetFieldValue<string[]>(10)));
        return rows;
    }

    public bool IsAssignable(string userName)
    {
        RefreshFromStudioSources();using var c=database.OpenConnection();
        using var q=new NpgsqlCommand("select exists(select 1 from studio_employees where lower(user_name)=lower($1) and is_active and is_assignable)",c);
        q.Parameters.AddWithValue(userName);return (bool)q.ExecuteScalar()!;
    }
    public IReadOnlyList<StudioEmployee> Search(string? query,int limit=25,bool photographersOnly=false)
    {
        string term=query?.Trim()??"";
        var candidates=GetAll(true).Where(x=>term.Length==0||x.DisplayName.Contains(term,StringComparison.OrdinalIgnoreCase)||x.UserName.Contains(term,StringComparison.OrdinalIgnoreCase));
        if(photographersOnly){var names=new Aegis.Studio.Web.Production.PhotographerStore(database).GetAll().Select(x=>x.UserName).ToHashSet(StringComparer.OrdinalIgnoreCase);candidates=candidates.Where(x=>names.Contains(x.UserName));}
        return candidates.Take(Math.Clamp(limit,1,50)).ToArray();
    }

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
        StudioEmployeeDirectoryStore store=app.Services.GetRequiredService<StudioEmployeeDirectoryStore>();store.EnsureSchema();
        app.MapGet("/api/studio-employees",(bool? assignable,string? q,int? limit,bool? photographersOnly)=>Results.Ok(assignable==true?store.Search(q,limit??25,photographersOnly==true):store.GetAll())).RequireAuthorization();
        RouteGroupBuilder admin=app.MapGroup("/api/admin/studio-employees").RequireAuthorization(new AuthorizeAttribute{Roles=StudioRoleNames.Administrator});
        admin.MapGet("/",()=>Results.Ok(store.GetAll()));
        admin.MapPost("/",(StudioEmployeeRequest request)=>Results.Ok(store.Save(request)));
        admin.MapPut("/{userName}",(string userName,StudioEmployeeRequest request)=>Results.Ok(store.Save(request with{UserName=userName})));
    }
}
public sealed record StudioEmployee(string UserName,string DisplayName,string? EmailAddress,bool IsActive,bool IsAssignable,string Source,string? ExternalId,IReadOnlyList<string> Skills,string? WorkDayUserName,DateTimeOffset? LastSyncedAtUtc,IReadOnlyList<string> Roles);
public sealed record StudioEmployeeRequest(string UserName,string? DisplayName,string? EmailAddress,bool IsActive=true,bool IsAssignable=true,string? Source="Manual",string? ExternalId=null,IReadOnlyList<string>? Skills=null,string? WorkDayUserName=null,DateTimeOffset? LastSyncedAtUtc=null);
