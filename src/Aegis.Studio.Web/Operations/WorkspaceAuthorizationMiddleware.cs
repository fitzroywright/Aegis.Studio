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

        WorkspaceAccessService workspaceAccess = new(database);
        JobAccessService jobAccess = new(database);
        AccessRequirement requirement = await ResolveRequirementAsync(context, database);
        if (requirement.WorkspaceId is null && requirement.JobId is null)
        {
            await next(context);
            return;
        }

        bool allowed = false;
        if (requirement.JobId.HasValue && !string.IsNullOrWhiteSpace(requirement.JobPermission))
        {
            allowed = jobAccess.HasPermission(context.User, requirement.JobId.Value, requirement.JobPermission);
        }

        if (!allowed && requirement.WorkspaceId.HasValue && !string.IsNullOrWhiteSpace(requirement.WorkspacePermission))
        {
            allowed = workspaceAccess.HasPermission(context.User, requirement.WorkspaceId.Value, requirement.WorkspacePermission);
        }

        if (!allowed)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "You do not have the required workspace or job permission." });
            return;
        }

        await next(context);
    }

    private static async Task<AccessRequirement> ResolveRequirementAsync(HttpContext context, StudioDatabase database)
    {
        string path = context.Request.Path.Value ?? string.Empty;
        string method = context.Request.Method;

        if (path.Equals("/api/conversations", StringComparison.OrdinalIgnoreCase) && method == "GET" && Guid.TryParse(context.Request.Query["workspaceId"], out Guid listWorkspace))
            return new AccessRequirement(listWorkspace,null,"View",null);

        if (path.Equals("/api/interactions", StringComparison.OrdinalIgnoreCase) && method == "POST")
            return new AccessRequirement(await ReadWorkspaceIdFromJsonBody(context),null,"ReplyRequester",null);

        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 3 && segments[1].Equals("conversations", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(segments[2], out Guid conversationId))
        {
            (Guid? workspaceId, Guid? jobId) = ResolveConversation(database, conversationId);
            if (segments.Length >= 4 && segments[3].Equals("messages", StringComparison.OrdinalIgnoreCase) && method == "POST") return new(workspaceId,jobId,"ReplyRequester","ReplyRequester");
            if (segments.Length >= 4 && segments[3].Equals("jobs", StringComparison.OrdinalIgnoreCase) && method == "POST") return new(workspaceId,jobId,"CreateJob",null);
            if (segments.Length >= 4 && segments[3].Equals("attachments", StringComparison.OrdinalIgnoreCase)) return new(workspaceId,jobId,method == "POST" ? "AddAttachments" : "ViewAttachments",method == "POST" ? "AddAttachments" : "ViewAttachments");
            return new(workspaceId,jobId,"View","ViewConversation");
        }

        if (segments.Length >= 3 && segments[1].Equals("jobs", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(segments[2], out Guid jobIdFromPath))
        {
            Guid? workspaceId = ResolveJobWorkspace(database, jobIdFromPath);
            if (segments.Length >= 4 && segments[3].Equals("financial-events", StringComparison.OrdinalIgnoreCase)) return new(workspaceId,jobIdFromPath,"EditJob","RecordFinancialActivity");
            if (segments.Length >= 4 && segments[3].Equals("pos", StringComparison.OrdinalIgnoreCase)) return new(workspaceId,jobIdFromPath,"EditJob",method == "GET" ? "ViewPosTransactions" : "UsePos");
            if (segments.Length >= 4 && segments[3].Equals("members", StringComparison.OrdinalIgnoreCase)) return new(workspaceId,jobIdFromPath,"ManageMembers","ManageJobMembers");
            if (segments.Length >= 4 && segments[3].Equals("notifications", StringComparison.OrdinalIgnoreCase)) return new(workspaceId,jobIdFromPath,"NotifyMembers","NotifyJobMembers");
            if (segments.Length >= 4 && segments[3].Equals("access", StringComparison.OrdinalIgnoreCase)) return new(workspaceId,jobIdFromPath,"View","AccessJob");
            return new(workspaceId,jobIdFromPath,method == "GET" ? "View" : "EditJob",method == "GET" ? "ViewJob" : "EditJob");
        }

        if (segments.Length >= 3 && segments[1].Equals("workspaces", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(segments[2], out Guid workspaceRouteId))
        {
            if (segments.Length >= 4 && segments[3].Equals("members", StringComparison.OrdinalIgnoreCase)) return new(workspaceRouteId,null,"ManageMembers",null);
            if (segments.Length >= 4 && segments[3].Equals("notifications", StringComparison.OrdinalIgnoreCase)) return new(workspaceRouteId,null,"NotifyMembers",null);
            return new(workspaceRouteId,null,"AccessWorkspace",null);
        }

        return new(null,null,null,null);
    }

    private static (Guid? WorkspaceId, Guid? JobId) ResolveConversation(StudioDatabase database, Guid conversationId)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select workspace_id,job_id from studio_conversations where id=$1";
        command.Parameters.AddWithValue(conversationId);
        using NpgsqlDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return (null,null);
        return (reader.GetGuid(0),reader.IsDBNull(1) ? null : reader.GetGuid(1));
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

    private sealed record AccessRequirement(Guid? WorkspaceId,Guid? JobId,string? WorkspacePermission,string? JobPermission);
}
