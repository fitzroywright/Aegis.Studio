using Aegis.Studio.Application.EmailRouting;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Storage;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace Aegis.Studio.Web.Email;

public static class LegacyMailboxImportEndpoints
{
    public static void MapLegacyMailboxImport(this WebApplication app)
    {
        app.MapPost("/api/workspaces/{workspaceId:guid}/email-import", async (
            Guid workspaceId,
            LegacyMailboxImportRequest request,
            HttpContext context,
            WorkspaceAccessService access,
            WorkspaceStore workspaces,
            StudioStore studio,
            AttachmentStore attachments,
            CancellationToken cancellationToken) =>
        {
            if (!access.HasPermission(context.User, workspaceId, "AdminWorkspace") && !access.HasPermission(context.User, workspaceId, "EditWorkspace")) return Results.Forbid();
            StudioWorkspaceDefinition? workspace = workspaces.Get(workspaceId);
            if (workspace is null) return Results.NotFound();
            try
            {
                LegacyMailboxImporter importer = new(studio, attachments);
                LegacyMailboxImportResult result = await importer.ImportAsync(workspace, request, cancellationToken);
                return Results.Ok(result);
            }
            catch (Exception exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).RequireAuthorization();
    }
}

public sealed class LegacyMailboxImporter
{
    private readonly StudioStore studio;
    private readonly AttachmentStore attachments;

    public LegacyMailboxImporter(StudioStore studio, AttachmentStore attachments)
    {
        this.studio = studio;
        this.attachments = attachments;
    }

    public async Task<LegacyMailboxImportResult> ImportAsync(StudioWorkspaceDefinition workspace, LegacyMailboxImportRequest request, CancellationToken cancellationToken)
    {
        ProviderSettings provider = ProviderSettings.Resolve(request.Provider);
        if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.Secret)) throw new InvalidOperationException("Mailbox user name and credential are required.");
        string authenticationMode = string.IsNullOrWhiteSpace(request.AuthenticationMode) ? provider.DefaultAuthenticationMode : request.AuthenticationMode.Trim();
        int maximum = Math.Clamp(request.MaximumMessages ?? 5000, 1, 50000);
        int imported = 0; int duplicates = 0; int attachmentCount = 0; int failed = 0;
        using ImapClient client = new();
        await client.ConnectAsync(provider.Host, provider.Port, SecureSocketOptions.SslOnConnect, cancellationToken);
        if (authenticationMode.Equals("OAuth2", StringComparison.OrdinalIgnoreCase))
        {
            await client.AuthenticateAsync(new SaslMechanismOAuth2(request.UserName.Trim(), request.Secret), cancellationToken);
        }
        else if (authenticationMode.Equals("Password", StringComparison.OrdinalIgnoreCase) || authenticationMode.Equals("AppPassword", StringComparison.OrdinalIgnoreCase))
        {
            await client.AuthenticateAsync(request.UserName.Trim(), request.Secret, cancellationToken);
        }
        else
        {
            throw new InvalidOperationException("AuthenticationMode must be OAuth2 or AppPassword.");
        }

        IMailFolder folder = string.IsNullOrWhiteSpace(request.Folder) ? client.Inbox : await client.GetFolderAsync(request.Folder.Trim(), cancellationToken);
        await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
        SearchQuery query = SearchQuery.All;
        if (request.FromUtc.HasValue) query = query.And(SearchQuery.DeliveredAfter(request.FromUtc.Value.UtcDateTime));
        if (request.ToUtc.HasValue) query = query.And(SearchQuery.DeliveredBefore(request.ToUtc.Value.UtcDateTime));
        IList<UniqueId> ids = await folder.SearchAsync(query, cancellationToken);
        foreach (UniqueId id in ids.TakeLast(maximum))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                MimeMessage message = await folder.GetMessageAsync(id, cancellationToken);
                string senderAddress = message.From.Mailboxes.FirstOrDefault()?.Address ?? request.UserName;
                string senderName = message.From.Mailboxes.FirstOrDefault()?.Name ?? senderAddress;
                string messageId = string.IsNullOrWhiteSpace(message.MessageId) ? $"legacy-{provider.Name}-{id.Id}@import.local" : message.MessageId;
                string body = message.TextBody ?? message.HtmlBody ?? string.Empty;
                InboundEmail inbound = new(
                    messageId,
                    message.InReplyTo,
                    senderAddress,
                    senderName,
                    message.To.Mailboxes.Select(x => x.Address).ToArray(),
                    message.Cc.Mailboxes.Select(x => x.Address).ToArray(),
                    message.Subject ?? "Imported message",
                    body,
                    message.Date == DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : message.Date.ToUniversalTime(),
                    new Dictionary<string,string>());
                EmailRouteResult route = new(true, workspace.Id, workspace.Name, request.UserName.Trim(), null);
                EmailIngestResult ingest = studio.ReceiveEmail(inbound, route);
                if (ingest.WasDuplicate)
                {
                    duplicates++;
                    continue;
                }
                imported++;
                foreach (MimeEntity entity in message.Attachments)
                {
                    if (entity is MimePart part)
                    {
                        using MemoryStream stream = new();
                        await part.Content.DecodeToAsync(stream, cancellationToken);
                        if (stream.Length > 0 && stream.Length <= attachments.MaximumBytes)
                        {
                            await attachments.AddBytesAsync(ingest.Conversation.Id, part.FileName ?? "attachment.bin", part.ContentType.MimeType, stream.ToArray(), "External", $"Mailbox import: {request.UserName}", cancellationToken);
                            attachmentCount++;
                        }
                    }
                    else if (entity is MessagePart attachedMessage)
                    {
                        using MemoryStream stream = new();
                        await attachedMessage.Message.WriteToAsync(stream, cancellationToken);
                        if (stream.Length > 0 && stream.Length <= attachments.MaximumBytes)
                        {
                            await attachments.AddBytesAsync(ingest.Conversation.Id, attachedMessage.FileName ?? "attached-message.eml", "message/rfc822", stream.ToArray(), "External", $"Mailbox import: {request.UserName}", cancellationToken);
                            attachmentCount++;
                        }
                    }
                }
            }
            catch
            {
                failed++;
            }
        }
        await client.DisconnectAsync(true, cancellationToken);
        return new LegacyMailboxImportResult(provider.Name, request.UserName.Trim(), folder.FullName, authenticationMode, ids.Count, imported, duplicates, attachmentCount, failed);
    }
}

public sealed record LegacyMailboxImportRequest(string Provider,string UserName,string Secret,string? AuthenticationMode,string? Folder,DateTimeOffset? FromUtc,DateTimeOffset? ToUtc,int? MaximumMessages);
public sealed record LegacyMailboxImportResult(string Provider,string Mailbox,string Folder,string AuthenticationMode,int MatchedMessages,int ImportedMessages,int DuplicateMessages,int ImportedAttachments,int FailedMessages);
internal sealed record ProviderSettings(string Name,string Host,int Port,string DefaultAuthenticationMode)
{
    public static ProviderSettings Resolve(string? provider) => provider?.Trim().ToLowerInvariant() switch
    {
        "gmail" => new("Gmail","imap.gmail.com",993,"AppPassword"),
        "hotmail" or "outlook" or "live" => new("Hotmail/Outlook","outlook.office365.com",993,"OAuth2"),
        "yahoo" => new("Yahoo","imap.mail.yahoo.com",993,"AppPassword"),
        _ => throw new InvalidOperationException("Provider must be Gmail, Hotmail/Outlook, or Yahoo.")
    };
}
