using System.Text.Json;
using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Operations;

public sealed class WorkspaceAuthorizationMiddleware
{
    private readonly RequestDelegate next;

    public WorkspaceAuthorizationMiddleware(RequestDelegate next)
    {
        this.next = next;
    }

    public async Task InvokeAsync(HttpContext context, StudioDatabase database)
    {
        if (context.User.Identity?.IsAuthenticated != true || context.User.IsInRole("Administrator") || !context.Request.Path.StartsWithSegments("/api"))
        {
            await next(context);
            return;
        }

        WorkspaceAccessService access = new(database);
        (Guid? workspaceId, string? permission) = await ResolveRequirementAsync(context, database);
        if (!workspaceId.HasValue || string.IsNullOrWhiteSpace(permission))
        {
            await next(context);
            return;
        }

        if (!access.HasPermission(context.User, workspaceId.Value, permission))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = $"Workspace permission '{permission}' is required." });
            return;
        }

        await next(context);
    }

    private static async Task<(Guid?, string?)> ResolveRequirementAsync(HttpContext context, StudioDatabase database)
    {
        string path = context.Request.Path.Value ?? string.Empty;
        string method = context.Request.Method;

        if (path.Equals("/api/conversations", StringComparison.OrdinalIgnoreCase) && method == "GET" && Guid.TryParse(context.Request.Query["workspaceId"], out Guid listWorkspace)) return (listWorkspace, "View");
        if (path.Equals("/api/interactions", StringComparison.OrdinalIgnoreCase) && method == "POST")
        {
            Guid? id = await ReadWorkspaceIdFromJsonBody(context);
            return (id, "ReplyRequester");
        }

        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 3 && segments[1].Equals("conversations", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(segments[2], out Guid conversationId))
        {
            Guid? workspaceId = ResolveConversationWorkspace(database, conversationId);
            if (segments.Length >= 4 && segments[3].Equals("messages", StringComparison.OrdinalIgnoreCase) && method == "POST") return (workspaceId, "ReplyRequester");
            if (segments.Length >= 4 && segments[3].Equals("jobs", StringComparison.OrdinalIgnoreCase) && method == "POST") return (workspaceId, "CreateJob");
            if (segments.Length >= 4 && segments[3].Equals("attachments", StringComparison.OrdinalIgnoreCase)) return (workspaceId, method == "POST" ? "AddAttachments" : "ViewAttachments");
            return (workspaceId, "View");
        }

        if (segments.Length >= 3 && segments[1].Equals("jobs", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(segments[2], out Guid jobId))
        {
            Guid? workspaceId = ResolveJobWorkspace(database, jobId);
            if (segments.Length >= 4 && segments[3].Equals("financial-events", StringComparison.OrdinalIgnoreCase)) return (workspaceId, "EditJob");
            if (segments.Length >= 4 && segments[3].Equals("pos", StringComparison.OrdinalIgnoreCase)) return (workspaceId, "EditJob");
            return (workspaceId, method == "GET" ? "View" : "EditJob");
        }

        if (segments.Length >= 3 && segments[1].Equals("workspaces", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(segments[2], out Guid workspaceRouteId))
        {
            if (segments.Length >= 4 && segments[3].Equals("members", StringComparison.OrdinalIgnoreCase)) return (workspaceRouteId, "ManageMembers");
            if (segments.Length >= 4 && segments[3].Equals("notifications", StringComparison.OrdinalIgnoreCase)) return (workspaceRouteId, "NotifyMembers");
            return (workspaceRouteId, "AccessWorkspace");
        }

        return (null, null);
    }

    private static Guid? ResolveConversationWorkspace(StudioDatabase database, Guid conversationId)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select workspace_id from studio_conversations where id=$1";
        command.Parameters.AddWithValue(conversationId);
        return command.ExecuteScalar() is Guid id ? id : null;
    }

    private static Guid? ResolveJobWorkspace(StudioDatabase database, Guid jobId)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select workspace_id from studio_jobs where id=$1";
        command.Parameters.AddWithValue(jobId);
        return command.ExecuteScalar() is Guid id ? id : null;
    }

    private static async Task<Guid?> ReadWorkspaceIdFromJsonBody(HttpContext context)
    {
        if (context.Request.ContentLength is null or 0 || context.Request.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) != true) return null;
        context.Request.EnableBuffering();
        using JsonDocument document = await JsonDocument.ParseAsync(context.Request.Body);
        context.Request.Body.Position = 0;
        if (document.RootElement.TryGetProperty("workspaceId", out JsonElement value) && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out Guid id)) return id;
        return null;
    }
}
