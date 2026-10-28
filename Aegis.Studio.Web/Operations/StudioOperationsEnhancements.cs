using Aegis.Studio.Domain.Contacts;
using Aegis.Studio.Web.FrontDesk;
using System.Security.Claims;
using Aegis.Studio.Web.Messaging;
using Aegis.Studio.Web.Notifications;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Storage;
using Npgsql;

namespace Aegis.Studio.Web.Operations;

public static class StudioOperationsEnhancements
{
    public const string FinanceRouteKey = "accounts-finance";

    public static void EnsureSchema(StudioDatabase database, WorkspaceStore workspaces)
    {
        using (NpgsqlConnection connection = database.OpenConnection())
        using (NpgsqlCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                create table if not exists studio_message_attachment_links
                (
                    attachment_id uuid primary key references studio_attachments(id) on delete cascade,
                    message_id uuid not null,
                    message_kind text not null,
                    job_id uuid null references studio_jobs(id) on delete cascade,
                    created_at_utc timestamptz not null default now()
                );
                create index if not exists ix_studio_message_attachment_links_message on studio_message_attachment_links(message_id);
                create index if not exists ix_studio_message_attachment_links_job on studio_message_attachment_links(job_id);
                """;
            command.CommandText += JobWorkspaceParticipation.Schema;
            command.ExecuteNonQuery();
        }

        // Retire the former system workspace while retaining every historical ID and record.
        using NpgsqlConnection retirementConnection = database.OpenConnection();
        using NpgsqlCommand retirement = retirementConnection.CreateCommand();
        retirement.CommandText = "update studio_workspaces set is_enabled=false, updated_at_utc=now() where lower(route_key)=$1 and is_enabled";
        retirement.Parameters.AddWithValue(FinanceRouteKey);
        retirement.ExecuteNonQuery();

    }

    public static void MapStudioOperationsEnhancements(this WebApplication app)
    {
        app.MapGet("/api/workflow/stages", (IConfiguration configuration) =>
        {
            return Results.Ok(Aegis.Studio.Web.Workflow.WorkflowRules.Stages);
        }).RequireAuthorization();

        app.MapPost("/api/frontdesk/clients/{clientId:guid}/jobs", (
            Guid clientId,
            FrontDeskCreateJobRequest request,
            ClaimsPrincipal user,
            StudioDatabase database,
            StudioStore studio) =>
        {
            GlobalOperationalRoleStore roles = new(database);
            if (!user.IsInRole(StudioRoleNames.Administrator) && !roles.HasPermission(user, StudioPermissions.ViewAllJobs))
                return Results.Forbid();
            Contact? client = new StudioClientStore(database).List().FirstOrDefault(x => x.Id == clientId);
            if (client is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(request.Title)) return Results.BadRequest(new { error = "Job title is required." });
            StudioConversation conversation = studio.ReceiveInteraction(new IncomingInteractionRequest(
                request.WorkspaceId,
                null,
                client.DisplayName,
                client.Email ?? client.Phone ?? "Front Desk",
                request.Title.Trim(),
                "Job created by Front Desk.",
                "Manual"));
            StudioJob job = studio.CreateJob(conversation.Id, new CreateJobRequest(request.Title.Trim(), request.EventDate));
            new StudioClientStore(database).Link(job.Id, clientId, user.FindFirstValue("login_name") ?? user.Identity?.Name ?? "Front Desk");
            return Results.Ok(studio.GetJob(job.Id));
        }).RequireAuthorization();

        app.MapPost("/api/frontdesk/jobs/{jobId:guid}/check-in", (
            Guid jobId,
            ClaimsPrincipal user,
            StudioDatabase database,
            StudioStore studio) =>
        {
            GlobalOperationalRoleStore roles = new(database);
            if (!user.IsInRole(StudioRoleNames.Administrator) && !roles.HasPermission(user, StudioPermissions.ViewAllJobs))
                return Results.Forbid();
            StudioJob? job = studio.GetJob(jobId);
            if (job is null) return Results.NotFound();
            if(job.Status is not ("Scheduled" or "Photography In Progress"))return Results.BadRequest(new{error="Only a booked session can be checked in. Open the job for its current next action."});
            // Client arrival is an audit event; only session start advances photography.
            StudioJob updated = job;
            using NpgsqlConnection connection = database.OpenConnection();
            using NpgsqlCommand audit = connection.CreateCommand();
            audit.CommandText = "insert into studio_operational_audit(id,job_id,activity_id,action,user_name,detail,created_at_utc) values($1,$2,null,'FrontDeskCheckIn',$3,$4,now())";
            audit.Parameters.AddWithValue(Guid.NewGuid());
            audit.Parameters.AddWithValue(jobId);
            audit.Parameters.AddWithValue(user.FindFirstValue("login_name") ?? user.Identity?.Name ?? "Front Desk");
            audit.Parameters.AddWithValue("Client checked in at Front Desk.");
            audit.ExecuteNonQuery();
            return Results.Ok(updated);
        }).RequireAuthorization();

        app.MapPost("/api/frontdesk/jobs/{jobId:guid}/communications", (
            Guid jobId,
            FrontDeskCommunicationRequest request,
            ClaimsPrincipal user,
            StudioDatabase database,
            StudioStore studio,
            WorkspaceStore workspaces,
            StudioInAppMessageStore messages,
            NotificationService notifications) =>
        {
            GlobalOperationalRoleStore roles = new(database);
            if (!user.IsInRole(StudioRoleNames.Administrator) && !roles.HasPermission(user, StudioPermissions.ViewAllJobs))
                return Results.Forbid();

            StudioJob? job = studio.GetJob(jobId);
            if (job is null) return Results.NotFound();
            StudioWorkspaceDefinition? workspace = workspaces.Get(job.WorkspaceId);
            if (workspace is null) return Results.NotFound();

            string kind = request.Kind?.Trim().ToLowerInvariant() ?? "workspace";
            if (kind is not ("workspace" or "escalate" or "finance"))
                return Results.BadRequest(new { error = "Communication kind must be workspace, escalate or finance." });
            if (string.IsNullOrWhiteSpace(request.Body))
                return Results.BadRequest(new { error = "Message is required." });

            WorkspaceAccessService workspaceAccess = new(database);
            string targetLabel;
            IReadOnlyList<(string UserName,string DisplayName)> recipients;

            if (kind == "finance")
            {
                GlobalOperationalRole? financeRole = roles.GetRoles().FirstOrDefault(x => x.IsActive && x.Name == StudioRoleNames.FinanceManager);
                recipients = financeRole is null ? [] : roles.GetMembers(financeRole.Id)
                    .Where(x => x.IsActive).Select(x => (x.UserName,x.DisplayName)).ToList();
                targetLabel = "Accounts / Finance";
            }
            else if (kind == "escalate" && string.Equals(request.EscalationTarget, "Operations Manager", StringComparison.OrdinalIgnoreCase))
            {
                GlobalOperationalRole? operationsRole = roles.GetRoles().FirstOrDefault(x => x.Name == StudioRoleNames.OperationsManager);
                recipients = operationsRole is null
                    ? []
                    : roles.GetMembers(operationsRole.Id).Where(x => x.IsActive).Select(x => (x.UserName,x.DisplayName)).ToList();
                targetLabel = StudioRoleNames.OperationsManager;
            }
            else if (kind == "escalate")
            {
                recipients = workspaceAccess.GetMembers(job.WorkspaceId)
                    .Where(x => x.IsActive && IsWorkspaceManager(x.Permissions))
                    .Select(x => (x.UserName,x.DisplayName)).ToList();
                targetLabel = "Workspace Manager";
            }
            else
            {
                recipients = workspaceAccess.GetMembers(job.WorkspaceId)
                    .Where(x => x.IsActive && x.Permissions.AccessWorkspace)
                    .Select(x => (x.UserName,x.DisplayName)).ToList();
                targetLabel = workspace.Name;
            }

            if (recipients.Count == 0)
                return Results.BadRequest(new { error = $"No active {targetLabel} recipient is configured." });

            StudioMessageThread thread = messages.GetOrCreateScoped(user, "Job", jobId);
            string context = $"Client: {job.LinkedClientName ?? job.ClientName} | Job: {job.Title} | Workspace: {workspace.Name} | Stage: {job.Status}";
            string prefix = kind == "finance"
                ? "[Accounts / Finance]"
                : kind == "escalate" ? $"[Escalation -> {targetLabel}]" : "[Front Desk -> Workspace]";
            StudioInAppMessage message = messages.AddMessage(thread.Id, user, $"{prefix} {context}\n\n{request.Body.Trim()}");

            foreach ((string userName,string displayName) in recipients.DistinctBy(x => x.UserName, StringComparer.OrdinalIgnoreCase))
            {
                notifications.PublishToUser(
                    userName,
                    kind == "escalate" ? "Escalation" : "WorkspaceMessage",
                    kind == "finance" ? $"Finance query: {job.Title}" : kind == "escalate" ? $"Escalation: {job.Title}" : $"Front Desk: {job.Title}",
                    request.Body.Trim(),
                    "Job",
                    jobId.ToString("D"));
            }

            using (NpgsqlConnection connection = database.OpenConnection())
            using (NpgsqlCommand audit = connection.CreateCommand())
            {
                audit.CommandText = "insert into studio_operational_audit(id,job_id,activity_id,action,user_name,detail,created_at_utc) values($1,$2,null,$3,$4,$5,now())";
                audit.Parameters.AddWithValue(Guid.NewGuid());
                audit.Parameters.AddWithValue(jobId);
                audit.Parameters.AddWithValue(kind == "finance" ? "FinanceQuery" : kind == "escalate" ? "Escalated" : "WorkspaceQuery");
                audit.Parameters.AddWithValue(user.FindFirstValue("login_name") ?? user.Identity?.Name ?? "Studio");
                audit.Parameters.AddWithValue($"{targetLabel}: {request.Body.Trim()}");
                audit.ExecuteNonQuery();
            }

            return Results.Ok(new { threadId = thread.Id, messageId = message.Id, target = targetLabel, recipients = recipients.Select(x => x.DisplayName) });
        }).RequireAuthorization();

        app.MapPost("/api/attachments/{attachmentId:guid}/message-link", (
            Guid attachmentId,
            MessageAttachmentLinkRequest request,
            ClaimsPrincipal user,
            AttachmentStore attachments,
            StudioStore studio,
            StudioDatabase database,
            WorkspaceAccessService workspaceAccess,
            JobAccessService jobAccess) =>
        {
            StudioAttachment? attachment = attachments.Get(attachmentId);
            if (attachment is null) return Results.NotFound();
            StudioConversation? conversation = studio.GetConversation(attachment.ConversationId);
            if (conversation is null) return Results.NotFound();
            bool allowed = workspaceAccess.HasPermission(user, conversation.WorkspaceId, "AddAttachments")
                || (conversation.JobId.HasValue && jobAccess.HasPermission(user, conversation.JobId.Value, "AddAttachments"));
            if (!allowed) return Results.Forbid();
            if (request.JobId.HasValue && conversation.JobId != request.JobId)
                return Results.BadRequest(new { error = "Attachment job context does not match the conversation." });

            using NpgsqlConnection connection = database.OpenConnection();
            using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText = """
                insert into studio_message_attachment_links(attachment_id,message_id,message_kind,job_id,created_at_utc)
                values($1,$2,$3,$4,now())
                on conflict(attachment_id) do update set message_id=excluded.message_id,message_kind=excluded.message_kind,job_id=excluded.job_id
                """;
            command.Parameters.AddWithValue(attachmentId);
            command.Parameters.AddWithValue(request.MessageId);
            command.Parameters.AddWithValue(string.IsNullOrWhiteSpace(request.MessageKind) ? "JobMessage" : request.MessageKind.Trim());
            command.Parameters.AddWithValue((object?)request.JobId ?? DBNull.Value);
            command.ExecuteNonQuery();
            return Results.NoContent();
        }).RequireAuthorization();

        app.MapGet("/api/conversations/{conversationId:guid}/files", (
            Guid conversationId,
            ClaimsPrincipal user,
            AttachmentStore attachments,
            StudioStore studio,
            StudioDatabase database,
            WorkspaceAccessService workspaceAccess,
            JobAccessService jobAccess) =>
        {
            StudioConversation? conversation = studio.GetConversation(conversationId);
            if (conversation is null) return Results.NotFound();
            bool allowed = workspaceAccess.HasPermission(user, conversation.WorkspaceId, "ViewAttachments")
                || (conversation.JobId.HasValue && jobAccess.HasPermission(user, conversation.JobId.Value, "ViewAttachments"));
            if (!allowed) return Results.Forbid();

            Dictionary<Guid,(Guid MessageId,string MessageKind,Guid? JobId)> links = [];
            using (NpgsqlConnection connection = database.OpenConnection())
            using (NpgsqlCommand command = connection.CreateCommand())
            {
                command.CommandText = """
                    select l.attachment_id,l.message_id,l.message_kind,l.job_id
                    from studio_message_attachment_links l
                    join studio_attachments a on a.id=l.attachment_id
                    where a.conversation_id=$1
                    """;
                command.Parameters.AddWithValue(conversationId);
                using NpgsqlDataReader reader = command.ExecuteReader();
                while (reader.Read())
                    links[reader.GetGuid(0)] = (reader.GetGuid(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetGuid(3));
            }

            return Results.Ok(attachments.GetForConversation(conversationId).Select(file =>
            {
                links.TryGetValue(file.Id, out var link);
                return new
                {
                    file.Id,
                    file.ConversationId,
                    file.FileName,
                    file.ContentType,
                    file.Size,
                    file.Visibility,
                    file.CreatedAt,
                    file.UploadedBy,
                    file.StorageStatus,
                    file.IsEmailAttachment,
                    messageId = link.MessageId == Guid.Empty ? (Guid?)null : link.MessageId,
                    messageKind = link.MessageId == Guid.Empty ? null : link.MessageKind,
                    jobId = link.JobId
                };
            }));
        }).RequireAuthorization();
    }

    private static bool IsWorkspaceManager(WorkspacePermissionSet permissions) =>
        permissions.AccessWorkspace &&
        permissions.View &&
        permissions.ManageSchedule &&
        permissions.ManageProduction &&
        permissions.ManageMembers &&
        !permissions.AdminWorkspace;
}

public sealed record FrontDeskCreateJobRequest(Guid WorkspaceId,string Title,DateOnly? EventDate);
public sealed record FrontDeskCommunicationRequest(string? Kind,string Body,string? EscalationTarget=null);
public sealed record MessageAttachmentLinkRequest(Guid MessageId,string? MessageKind,Guid? JobId);


