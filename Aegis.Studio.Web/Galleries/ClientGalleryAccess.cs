using System.Security.Cryptography;
using System.Text;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Galleries;

public static class ClientGalleryAccessEndpoints
{
    public static void MapStudioClientGalleryAccess(this WebApplication app)
    {
        StudioDatabase database = app.Services.GetRequiredService<StudioDatabase>();
        ClientGalleryAccessStore accessStore = new(database);
        accessStore.EnsureSchema();

        app.Use(async (context, next) =>
        {
            if (TryGetPublicGalleryToken(context.Request.Path, out string? token)
                && !context.Request.Path.StartsWithSegments("/public/galleries/auth")
                && accessStore.RequiresAuthentication(token!))
            {
                string sessionToken = context.Request.Cookies["StudioGalleryAccess"] ?? string.Empty;
                if (!accessStore.ValidateSession(token!, sessionToken))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    if (context.Request.Path.StartsWithSegments("/public/galleries"))
                    {
                        await context.Response.WriteAsJsonAsync(new { error = "Gallery authentication is required.", authenticationRequired = true });
                    }
                    return;
                }
            }
            await next();
        });

        app.MapGet("/api/workspaces/{workspaceId:guid}/gallery/access", (Guid workspaceId, HttpContext context, WorkspaceAccessService workspaceAccess) =>
        {
            if (!context.User.IsInRole("Administrator") && !workspaceAccess.HasPermission(context.User, workspaceId, "EditWorkspace") && !workspaceAccess.HasPermission(context.User, workspaceId, "AdminWorkspace")) return Results.Forbid();
            return Results.Ok(accessStore.GetSettings(workspaceId));
        }).RequireAuthorization();

        app.MapPut("/api/workspaces/{workspaceId:guid}/gallery/access", (Guid workspaceId, GalleryAccessSettingsRequest request, HttpContext context, WorkspaceAccessService workspaceAccess) =>
        {
            if (!context.User.IsInRole("Administrator") && !workspaceAccess.HasPermission(context.User, workspaceId, "EditWorkspace") && !workspaceAccess.HasPermission(context.User, workspaceId, "AdminWorkspace")) return Results.Forbid();
            try { return Results.Ok(accessStore.SaveSettings(workspaceId, request)); }
            catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
        }).RequireAuthorization();

        app.MapPost("/public/galleries/auth/{token}", (string token, GalleryLoginRequest request, HttpContext context) =>
        {
            if (!accessStore.Authenticate(token, request.Password, out string sessionToken, out DateTimeOffset expiresAtUtc)) return Results.Unauthorized();
            context.Response.Cookies.Append("StudioGalleryAccess", sessionToken, new CookieOptions
            {
                HttpOnly = true,
                Secure = context.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Expires = expiresAtUtc,
                IsEssential = true
            });
            return Results.Ok(new { authenticated = true, expiresAtUtc });
        }).AllowAnonymous();

        app.MapPost("/public/galleries/auth/{token}/logout", (string token, HttpContext context) =>
        {
            string sessionToken = context.Request.Cookies["StudioGalleryAccess"] ?? string.Empty;
            accessStore.RevokeSession(token, sessionToken);
            context.Response.Cookies.Delete("StudioGalleryAccess");
            return Results.Ok(new { authenticated = false });
        }).AllowAnonymous();

        app.MapGet("/public/galleries/{token}/selections", (string token, HttpContext context) =>
        {
            if (!accessStore.CanUsePublicGallery(token, context.Request.Cookies["StudioGalleryAccess"] ?? string.Empty)) return Results.Unauthorized();
            return Results.Ok(accessStore.GetSelections(token));
        }).AllowAnonymous();

        app.MapPut("/public/galleries/{token}/selections/{itemId:guid}", (string token, Guid itemId, GallerySelectionRequest request, HttpContext context) =>
        {
            if (!accessStore.CanUsePublicGallery(token, context.Request.Cookies["StudioGalleryAccess"] ?? string.Empty)) return Results.Unauthorized();
            try { return Results.Ok(accessStore.SaveSelection(token, itemId, request)); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        }).AllowAnonymous();

        app.MapPost("/public/galleries/{token}/selections/submit", (string token, GallerySelectionSubmitRequest request, HttpContext context) =>
        {
            if (!accessStore.CanUsePublicGallery(token, context.Request.Cookies["StudioGalleryAccess"] ?? string.Empty)) return Results.Unauthorized();
            return Results.Ok(accessStore.SubmitSelections(token, request));
        }).AllowAnonymous();
    }

    private static bool TryGetPublicGalleryToken(PathString path, out string? token)
    {
        token = null;
        string value = path.Value ?? string.Empty;
        string[] segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 3 && segments[0].Equals("public", StringComparison.OrdinalIgnoreCase) && segments[1].Equals("galleries", StringComparison.OrdinalIgnoreCase))
        {
            token = segments[2];
            return true;
        }
        return false;
    }
}

public sealed class ClientGalleryAccessStore
{
    private readonly StudioDatabase database;

    public ClientGalleryAccessStore(StudioDatabase database) => this.database = database;

    public void EnsureSchema()
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            create table if not exists studio_gallery_access
            (
                gallery_id uuid primary key references studio_workspace_galleries(id) on delete cascade,
                require_password boolean not null default false,
                password_hash text null,
                password_salt text null,
                session_hours integer not null default 24,
                selections_enabled boolean not null default true,
                selection_limit integer null,
                updated_at_utc timestamptz not null
            );
            create table if not exists studio_gallery_sessions
            (
                id uuid primary key,
                gallery_id uuid not null references studio_workspace_galleries(id) on delete cascade,
                token_hash text not null unique,
                expires_at_utc timestamptz not null,
                revoked_at_utc timestamptz null,
                created_at_utc timestamptz not null
            );
            create index if not exists ix_studio_gallery_sessions_lookup on studio_gallery_sessions(gallery_id,token_hash,expires_at_utc);
            create table if not exists studio_gallery_selections
            (
                gallery_id uuid not null references studio_workspace_galleries(id) on delete cascade,
                item_id uuid not null references studio_gallery_items(id) on delete cascade,
                selected boolean not null default true,
                note text null,
                updated_at_utc timestamptz not null,
                primary key(gallery_id,item_id)
            );
            create table if not exists studio_gallery_selection_submissions
            (
                id uuid primary key,
                gallery_id uuid not null references studio_workspace_galleries(id) on delete cascade,
                client_name text null,
                client_email text null,
                note text null,
                selection_count integer not null,
                submitted_at_utc timestamptz not null
            );
            """;
        command.ExecuteNonQuery();
    }

    public GalleryAccessSettings GetSettings(Guid workspaceId)
    {
        Guid galleryId = GetGalleryIdByWorkspace(workspaceId) ?? throw new KeyNotFoundException("Gallery was not found.");
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select require_password,session_hours,selections_enabled,selection_limit,password_hash is not null from studio_gallery_access where gallery_id=$1";
        command.Parameters.AddWithValue(galleryId);
        using NpgsqlDataReader reader = command.ExecuteReader();
        return reader.Read()
            ? new GalleryAccessSettings(reader.GetBoolean(0),reader.GetInt32(1),reader.GetBoolean(2),reader.IsDBNull(3)?null:reader.GetInt32(3),reader.GetBoolean(4))
            : new GalleryAccessSettings(false,24,true,null,false);
    }

    public GalleryAccessSettings SaveSettings(Guid workspaceId, GalleryAccessSettingsRequest request)
    {
        Guid galleryId = GetGalleryIdByWorkspace(workspaceId) ?? throw new KeyNotFoundException("Gallery was not found.");
        if (request.SessionHours < 1 || request.SessionHours > 720) throw new InvalidOperationException("Session hours must be between 1 and 720.");
        if (request.SelectionLimit.HasValue && request.SelectionLimit.Value < 1) throw new InvalidOperationException("Selection limit must be positive.");

        string? passwordHash = null;
        string? passwordSalt = null;
        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            byte[] hash = Rfc2898DeriveBytes.Pbkdf2(request.Password, salt, 210000, HashAlgorithmName.SHA256, 32);
            passwordSalt = Convert.ToBase64String(salt);
            passwordHash = Convert.ToBase64String(hash);
        }

        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            insert into studio_gallery_access(gallery_id,require_password,password_hash,password_salt,session_hours,selections_enabled,selection_limit,updated_at_utc)
            values($1,$2,$3,$4,$5,$6,$7,now())
            on conflict(gallery_id) do update set
              require_password=excluded.require_password,
              password_hash=case when excluded.password_hash is null then studio_gallery_access.password_hash else excluded.password_hash end,
              password_salt=case when excluded.password_salt is null then studio_gallery_access.password_salt else excluded.password_salt end,
              session_hours=excluded.session_hours,selections_enabled=excluded.selections_enabled,selection_limit=excluded.selection_limit,updated_at_utc=now()
            """;
        command.Parameters.AddWithValue(galleryId); command.Parameters.AddWithValue(request.RequirePassword);
        command.Parameters.AddWithValue((object?)passwordHash ?? DBNull.Value); command.Parameters.AddWithValue((object?)passwordSalt ?? DBNull.Value);
        command.Parameters.AddWithValue(request.SessionHours); command.Parameters.AddWithValue(request.SelectionsEnabled); command.Parameters.AddWithValue((object?)request.SelectionLimit ?? DBNull.Value);
        command.ExecuteNonQuery();
        return GetSettings(workspaceId);
    }

    public bool RequiresAuthentication(string publicToken)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select coalesce(a.require_password,false) from studio_workspace_galleries g left join studio_gallery_access a on a.gallery_id=g.id where g.public_token=$1 and g.is_published";
        command.Parameters.AddWithValue(publicToken);
        object? value = command.ExecuteScalar();
        return value is bool result && result;
    }

    public bool Authenticate(string publicToken, string? password, out string sessionToken, out DateTimeOffset expiresAtUtc)
    {
        sessionToken = string.Empty; expiresAtUtc = DateTimeOffset.MinValue;
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select g.id,coalesce(a.require_password,false),a.password_hash,a.password_salt,coalesce(a.session_hours,24) from studio_workspace_galleries g left join studio_gallery_access a on a.gallery_id=g.id where g.public_token=$1 and g.is_published";
        command.Parameters.AddWithValue(publicToken);
        using NpgsqlDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return false;
        Guid galleryId = reader.GetGuid(0); bool required = reader.GetBoolean(1); string? hashText = reader.IsDBNull(2)?null:reader.GetString(2); string? saltText = reader.IsDBNull(3)?null:reader.GetString(3); int hours = reader.GetInt32(4);
        if (required)
        {
            if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(hashText) || string.IsNullOrEmpty(saltText)) return false;
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(saltText), 210000, HashAlgorithmName.SHA256, 32);
            byte[] expected = Convert.FromBase64String(hashText);
            if (!CryptographicOperations.FixedTimeEquals(actual, expected)) return false;
        }
        reader.Close();
        sessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        expiresAtUtc = DateTimeOffset.UtcNow.AddHours(hours);
        using NpgsqlCommand insert = connection.CreateCommand();
        insert.CommandText = "insert into studio_gallery_sessions(id,gallery_id,token_hash,expires_at_utc,created_at_utc) values($1,$2,$3,$4,now())";
        insert.Parameters.AddWithValue(Guid.NewGuid()); insert.Parameters.AddWithValue(galleryId); insert.Parameters.AddWithValue(HashToken(sessionToken)); insert.Parameters.AddWithValue(expiresAtUtc); insert.ExecuteNonQuery();
        return true;
    }

    public bool ValidateSession(string publicToken, string sessionToken)
    {
        if (string.IsNullOrWhiteSpace(sessionToken)) return false;
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select exists(select 1 from studio_gallery_sessions s join studio_workspace_galleries g on g.id=s.gallery_id where g.public_token=$1 and s.token_hash=$2 and s.revoked_at_utc is null and s.expires_at_utc>now())";
        command.Parameters.AddWithValue(publicToken); command.Parameters.AddWithValue(HashToken(sessionToken));
        return command.ExecuteScalar() is bool value && value;
    }

    public bool CanUsePublicGallery(string token, string sessionToken) => !RequiresAuthentication(token) || ValidateSession(token, sessionToken);

    public void RevokeSession(string token, string sessionToken)
    {
        if (string.IsNullOrWhiteSpace(sessionToken)) return;
        using NpgsqlConnection connection = database.OpenConnection(); using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "update studio_gallery_sessions s set revoked_at_utc=now() from studio_workspace_galleries g where s.gallery_id=g.id and g.public_token=$1 and s.token_hash=$2";
        command.Parameters.AddWithValue(token); command.Parameters.AddWithValue(HashToken(sessionToken)); command.ExecuteNonQuery();
    }

    public IReadOnlyList<GallerySelection> GetSelections(string token)
    {
        using NpgsqlConnection connection = database.OpenConnection(); using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select s.item_id,s.selected,s.note,s.updated_at_utc from studio_gallery_selections s join studio_workspace_galleries g on g.id=s.gallery_id where g.public_token=$1 order by s.updated_at_utc";
        command.Parameters.AddWithValue(token); using NpgsqlDataReader reader=command.ExecuteReader(); List<GallerySelection> values=[]; while(reader.Read()) values.Add(new(reader.GetGuid(0),reader.GetBoolean(1),reader.IsDBNull(2)?null:reader.GetString(2),reader.GetFieldValue<DateTimeOffset>(3))); return values;
    }

    public GallerySelection SaveSelection(string token, Guid itemId, GallerySelectionRequest request)
    {
        using NpgsqlConnection connection=database.OpenConnection();
        Guid galleryId; int? limit;
        using(NpgsqlCommand lookup=connection.CreateCommand())
        {
            lookup.CommandText="select g.id,a.selection_limit from studio_workspace_galleries g left join studio_gallery_access a on a.gallery_id=g.id where g.public_token=$1 and g.is_published and coalesce(a.selections_enabled,true)"; lookup.Parameters.AddWithValue(token);
            using NpgsqlDataReader reader=lookup.ExecuteReader(); if(!reader.Read()) throw new KeyNotFoundException(); galleryId=reader.GetGuid(0); limit=reader.IsDBNull(1)?null:reader.GetInt32(1);
        }
        using(NpgsqlCommand item=connection.CreateCommand()){item.CommandText="select exists(select 1 from studio_gallery_items where id=$1 and gallery_id=$2 and is_visible)";item.Parameters.AddWithValue(itemId);item.Parameters.AddWithValue(galleryId);if(item.ExecuteScalar() is not bool exists||!exists)throw new KeyNotFoundException();}
        if(request.Selected&&limit.HasValue){using NpgsqlCommand count=connection.CreateCommand();count.CommandText="select count(*) from studio_gallery_selections where gallery_id=$1 and selected and item_id<>$2";count.Parameters.AddWithValue(galleryId);count.Parameters.AddWithValue(itemId);if(Convert.ToInt32(count.ExecuteScalar())>=limit.Value)throw new InvalidOperationException($"This gallery allows up to {limit.Value} selections.");}
        using NpgsqlCommand command=connection.CreateCommand();command.CommandText="insert into studio_gallery_selections(gallery_id,item_id,selected,note,updated_at_utc) values($1,$2,$3,$4,now()) on conflict(gallery_id,item_id) do update set selected=excluded.selected,note=excluded.note,updated_at_utc=now()";command.Parameters.AddWithValue(galleryId);command.Parameters.AddWithValue(itemId);command.Parameters.AddWithValue(request.Selected);command.Parameters.AddWithValue((object?)request.Note?.Trim()??DBNull.Value);command.ExecuteNonQuery();return GetSelections(token).First(x=>x.ItemId==itemId);
    }

    public GallerySelectionSubmission SubmitSelections(string token, GallerySelectionSubmitRequest request)
    {
        using NpgsqlConnection connection=database.OpenConnection(); using NpgsqlCommand lookup=connection.CreateCommand(); lookup.CommandText="select id from studio_workspace_galleries where public_token=$1 and is_published";lookup.Parameters.AddWithValue(token);object? value=lookup.ExecuteScalar();if(value is not Guid galleryId)throw new KeyNotFoundException();
        using NpgsqlCommand count=connection.CreateCommand();count.CommandText="select count(*) from studio_gallery_selections where gallery_id=$1 and selected";count.Parameters.AddWithValue(galleryId);int selectionCount=Convert.ToInt32(count.ExecuteScalar());if(selectionCount==0)throw new InvalidOperationException("Select at least one image before submitting.");
        Guid id=Guid.NewGuid();using NpgsqlCommand insert=connection.CreateCommand();insert.CommandText="insert into studio_gallery_selection_submissions(id,gallery_id,client_name,client_email,note,selection_count,submitted_at_utc) values($1,$2,$3,$4,$5,$6,now())";insert.Parameters.AddWithValue(id);insert.Parameters.AddWithValue(galleryId);insert.Parameters.AddWithValue((object?)request.ClientName?.Trim()??DBNull.Value);insert.Parameters.AddWithValue((object?)request.ClientEmail?.Trim()??DBNull.Value);insert.Parameters.AddWithValue((object?)request.Note?.Trim()??DBNull.Value);insert.Parameters.AddWithValue(selectionCount);insert.ExecuteNonQuery();return new GallerySelectionSubmission(id,galleryId,request.ClientName,request.ClientEmail,request.Note,selectionCount,DateTimeOffset.UtcNow);
    }

    private Guid? GetGalleryIdByWorkspace(Guid workspaceId){using NpgsqlConnection c=database.OpenConnection();using NpgsqlCommand q=c.CreateCommand();q.CommandText="select id from studio_workspace_galleries where workspace_id=$1";q.Parameters.AddWithValue(workspaceId);object? v=q.ExecuteScalar();return v is Guid id?id:null;}
    private static string HashToken(string token)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}

public sealed record GalleryAccessSettings(bool RequirePassword,int SessionHours,bool SelectionsEnabled,int? SelectionLimit,bool HasPassword);
public sealed record GalleryAccessSettingsRequest(bool RequirePassword,string? Password,int SessionHours=24,bool SelectionsEnabled=true,int? SelectionLimit=null);
public sealed record GalleryLoginRequest(string? Password);
public sealed record GallerySelectionRequest(bool Selected,string? Note=null);
public sealed record GallerySelection(Guid ItemId,bool Selected,string? Note,DateTimeOffset UpdatedAtUtc);
public sealed record GallerySelectionSubmitRequest(string? ClientName,string? ClientEmail,string? Note);
public sealed record GallerySelectionSubmission(Guid Id,Guid GalleryId,string? ClientName,string? ClientEmail,string? Note,int SelectionCount,DateTimeOffset SubmittedAtUtc);
