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
            create table if not exists studio_employee_photos(
              user_name text not null,
              source text not null,
              content_type text not null,
              file_name text null,
              content bytea not null,
              updated_at_utc timestamptz not null default now(),
              primary key(user_name,source)
            );
            create index if not exists ix_studio_employee_photos_user on studio_employee_photos(lower(user_name),updated_at_utc desc);
            """;
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<StudioEmployee> GetAll(bool assignableOnly=false)
    {
        using var c=database.OpenConnection();using var cmd=c.CreateCommand();
        cmd.CommandText="""
            select e.user_name,e.display_name,e.email_address,e.is_active,e.is_assignable,e.source,e.external_id,e.skills,e.workday_user_name,e.last_synced_at_utc,
              coalesce(array(select distinct r.name from studio_global_operational_role_members m join studio_global_operational_roles r on r.id=m.role_id
                where m.is_active and r.is_active and lower(m.user_name)=lower(e.user_name) order by r.name),'{}'),photo.source,photo.updated_at_utc
            from (
              select distinct on (lower(user_name)) user_name,display_name,email_address,is_active,is_assignable,source,external_id,skills,workday_user_name,last_synced_at_utc
              from (
                select 0 priority,user_name,display_name,email_address,is_active,is_assignable,source,external_id,skills,workday_user_name,last_synced_at_utc from studio_employees
                union all select 1,user_name,display_name,null,is_active,is_active,'Studio photographer',null,skills,user_name,null from studio_photographers
                union all select 2,user_name,display_name,null,is_active,true,'Studio role member',null,'{}'::text[],null,null from studio_global_operational_role_members
                union all select 3,user_name,display_name,null,is_active,true,'Workspace member',null,'{}'::text[],null,null from studio_workspace_members
                union all select 4,user_name,display_name,email,is_active,true,'Job Role',null,'{}'::text[],null,null from studio_workers
              ) identities order by lower(user_name),priority,display_name
            ) e left join lateral(
              select source,updated_at_utc from studio_employee_photos p where lower(p.user_name)=lower(e.user_name)
              order by case when lower(source)='studio' then 0 else 1 end,updated_at_utc desc limit 1
            ) photo on true
            where not $1 or (e.is_active and e.is_assignable) order by e.display_name,e.user_name
            """;
        cmd.Parameters.AddWithValue(assignableOnly);
        using var r=cmd.ExecuteReader();List<StudioEmployee> rows=[];
        while(r.Read())
        {
            string? photoSource=r.IsDBNull(11)?null:r.GetString(11);
            DateTimeOffset? photoUpdated=r.IsDBNull(12)?null:r.GetFieldValue<DateTimeOffset>(12);
            string? photoUrl=photoSource is null?null:$"/api/studio-employees/{Uri.EscapeDataString(r.GetString(0))}/photo?v={photoUpdated?.ToUnixTimeMilliseconds()}";
            rows.Add(new(r.GetString(0),r.GetString(1),r.IsDBNull(2)?null:r.GetString(2),r.GetBoolean(3),r.GetBoolean(4),r.GetString(5),r.IsDBNull(6)?null:r.GetString(6),r.GetFieldValue<string[]>(7),r.IsDBNull(8)?null:r.GetString(8),r.IsDBNull(9)?null:r.GetFieldValue<DateTimeOffset>(9),r.GetFieldValue<string[]>(10),photoUrl,photoSource));
        }
        return rows;
    }

    public bool IsAssignable(string userName)
        => GetAll(true).Any(person => person.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase));
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
    public StudioEmployeePhoto? GetPhoto(string userName)
    {
        using var c=database.OpenConnection();using var cmd=c.CreateCommand();
        cmd.CommandText="""
          select source,content_type,file_name,content,updated_at_utc from studio_employee_photos
          where lower(user_name)=lower($1)
          order by case when lower(source)='studio' then 0 else 1 end,updated_at_utc desc limit 1
          """;
        cmd.Parameters.AddWithValue(userName);using var r=cmd.ExecuteReader();
        return r.Read()?new(r.GetString(0),r.GetString(1),r.IsDBNull(2)?null:r.GetString(2),r.GetFieldValue<byte[]>(3),r.GetFieldValue<DateTimeOffset>(4)):null;
    }
    public void SavePhoto(string userName,string source,string contentType,string? fileName,byte[] content)
    {
        if(!GetAll().Any(x=>x.UserName.Equals(userName,StringComparison.OrdinalIgnoreCase)))throw new KeyNotFoundException("Staff member was not found.");
        if(string.IsNullOrWhiteSpace(source))throw new ArgumentException("Photo source is required.");
        if(content.Length==0||content.Length>StaffPhotoValidator.MaximumBytes)throw new ArgumentException("Staff photos must be no larger than 5 MB.");
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        using(var remove=new NpgsqlCommand("delete from studio_employee_photos where lower(user_name)=lower($1) and lower(source)=lower($2)",c,tx)){remove.Parameters.AddWithValue(userName);remove.Parameters.AddWithValue(source);remove.ExecuteNonQuery();}
        using(var insert=new NpgsqlCommand("insert into studio_employee_photos(user_name,source,content_type,file_name,content,updated_at_utc) values($1,$2,$3,$4,$5,now())",c,tx))
        {insert.Parameters.AddWithValue(userName);insert.Parameters.AddWithValue(source.Trim());insert.Parameters.AddWithValue(contentType);insert.Parameters.AddWithValue((object?)Clean(fileName)??DBNull.Value);insert.Parameters.AddWithValue(content);insert.ExecuteNonQuery();}
        tx.Commit();
    }
    public bool RemovePhoto(string userName,string source="Studio")
    {
        using var c=database.OpenConnection();using var cmd=c.CreateCommand();cmd.CommandText="delete from studio_employee_photos where lower(user_name)=lower($1) and lower(source)=lower($2)";cmd.Parameters.AddWithValue(userName);cmd.Parameters.AddWithValue(source);return cmd.ExecuteNonQuery()>0;
    }
    private static string? Clean(string? value)=>string.IsNullOrWhiteSpace(value)?null:value.Trim();
}

public static class StudioEmployeeDirectoryEndpoints
{
    public static void MapStudioEmployeeDirectory(this WebApplication app)
    {
        StudioEmployeeDirectoryStore store=app.Services.GetRequiredService<StudioEmployeeDirectoryStore>();store.EnsureSchema();
        app.MapGet("/api/studio-employees",(bool? assignable,string? q,int? limit,bool? photographersOnly)=>Results.Ok(assignable==true?store.Search(q,limit??25,photographersOnly==true):store.GetAll())).RequireAuthorization();
        app.MapGet("/api/studio-employees/{userName}/photo",(string userName,HttpContext context)=>
        {
            StudioEmployeePhoto? photo=store.GetPhoto(userName);if(photo is null)return Results.NotFound();
            context.Response.Headers.CacheControl="private,max-age=86400";
            return Results.File(photo.Content,photo.ContentType,lastModified:photo.UpdatedAtUtc);
        }).RequireAuthorization();
        RouteGroupBuilder admin=app.MapGroup("/api/admin/studio-employees").RequireAuthorization(new AuthorizeAttribute{Roles=StudioRoleNames.Administrator});
        admin.MapGet("/",()=>Results.Ok(store.GetAll()));
        admin.MapPost("/",(StudioEmployeeRequest request)=>Results.Ok(store.Save(request)));
        admin.MapPut("/{userName}",(string userName,StudioEmployeeRequest request)=>Results.Ok(store.Save(request with{UserName=userName})));
        admin.MapPost("/{userName}/photo",async (string userName,IFormFile file,CancellationToken token)=>
        {
            if(file.Length==0||file.Length>StaffPhotoValidator.MaximumBytes)return Results.BadRequest(new{error="Choose a JPEG, PNG or WebP image no larger than 5 MB."});
            await using var memory=new MemoryStream();await file.CopyToAsync(memory,token);byte[] content=memory.ToArray();
            string? contentType=StaffPhotoValidator.DetectContentType(content);if(contentType is null)return Results.BadRequest(new{error="Choose a valid JPEG, PNG or WebP image."});
            try{store.SavePhoto(userName,"Studio",contentType,file.FileName,content);return Results.Ok(store.GetAll().First(x=>x.UserName.Equals(userName,StringComparison.OrdinalIgnoreCase)));}
            catch(KeyNotFoundException error){return Results.NotFound(new{error=error.Message});}
        }).DisableAntiforgery();
        admin.MapDelete("/{userName}/photo",(string userName)=>store.RemovePhoto(userName)?Results.NoContent():Results.NotFound());
    }
}
public sealed record StudioEmployee(string UserName,string DisplayName,string? EmailAddress,bool IsActive,bool IsAssignable,string Source,string? ExternalId,IReadOnlyList<string> Skills,string? WorkDayUserName,DateTimeOffset? LastSyncedAtUtc,IReadOnlyList<string> Roles,string? PhotoUrl,string? PhotoSource);
public sealed record StudioEmployeeRequest(string UserName,string? DisplayName,string? EmailAddress,bool IsActive=true,bool IsAssignable=true,string? Source="Manual",string? ExternalId=null,IReadOnlyList<string>? Skills=null,string? WorkDayUserName=null,DateTimeOffset? LastSyncedAtUtc=null);
public sealed record StudioEmployeePhoto(string Source,string ContentType,string? FileName,byte[] Content,DateTimeOffset UpdatedAtUtc);

public static class StaffPhotoValidator
{
    public const int MaximumBytes=5*1024*1024;
    public static string? DetectContentType(ReadOnlySpan<byte> content)
    {
        if(content.Length>=3&&content[0]==0xff&&content[1]==0xd8&&content[2]==0xff)return "image/jpeg";
        if(content.Length>=8&&content[..8].SequenceEqual(new byte[]{0x89,0x50,0x4e,0x47,0x0d,0x0a,0x1a,0x0a}))return "image/png";
        if(content.Length>=12&&content[..4].SequenceEqual("RIFF"u8)&&content.Slice(8,4).SequenceEqual("WEBP"u8))return "image/webp";
        return null;
    }
}
