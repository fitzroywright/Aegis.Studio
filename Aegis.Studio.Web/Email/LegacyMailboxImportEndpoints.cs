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
            StudioEmailIntake intake,
            AttachmentStore attachments,
            CancellationToken cancellationToken) =>
        {
            if (!access.HasPermission(context.User, workspaceId, "AdminWorkspace") && !access.HasPermission(context.User, workspaceId, "EditWorkspace")) return Results.Forbid();
            StudioWorkspaceDefinition? workspace = workspaces.Get(workspaceId);
            if (workspace is null) return Results.NotFound();
            try
            {
                LegacyMailboxImporter importer = new(attachments, intake);
                LegacyMailboxImportResult result = await importer.ImportAsync(workspace, request, cancellationToken, context.User);
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
    private readonly StudioEmailIntake intake;
    private readonly AttachmentStore attachments;

    public LegacyMailboxImporter(AttachmentStore attachments, StudioEmailIntake intake)
    {
        this.intake = intake;
        this.attachments = attachments;
    }

    public async Task<LegacyMailboxImportResult> ImportAsync(StudioWorkspaceDefinition workspace, LegacyMailboxImportRequest request, CancellationToken cancellationToken, System.Security.Claims.ClaimsPrincipal? user = null)
    {
        ProviderSettings provider = ProviderSettings.Resolve(request.Provider);
        if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.Secret)) throw new InvalidOperationException("Mailbox user name and credential are required.");

        string userName = request.UserName.Trim();
        string credential = request.Secret;
        string? requestedAuthenticationMode = request.AuthenticationMode;
        string authenticationMode = string.IsNullOrWhiteSpace(requestedAuthenticationMode)
            ? provider.DefaultAuthenticationMode
            : requestedAuthenticationMode.Trim();
        string? requestedFolder = request.Folder;

        int maximum = Math.Clamp(request.MaximumMessages ?? 5000, 1, 50000);
        int imported = 0;
        int duplicates = 0;
        int attachmentCount = 0;
        int failed = 0;

        using ImapClient client = new();
        await client.ConnectAsync(provider.Host, provider.Port, SecureSocketOptions.SslOnConnect, cancellationToken);
        if (authenticationMode.Equals("OAuth2", StringComparison.OrdinalIgnoreCase))
        {
            await client.AuthenticateAsync(new SaslMechanismOAuth2(userName, credential), cancellationToken);
        }
        else if (authenticationMode.Equals("Password", StringComparison.OrdinalIgnoreCase) || authenticationMode.Equals("AppPassword", StringComparison.OrdinalIgnoreCase))
        {
            await client.AuthenticateAsync(userName, credential, cancellationToken);
        }
        else
        {
            throw new InvalidOperationException("AuthenticationMode must be OAuth2 or AppPassword.");
        }

        IMailFolder folder = string.IsNullOrWhiteSpace(requestedFolder)
            ? client.Inbox
            : await client.GetFolderAsync(requestedFolder.Trim(), cancellationToken);
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
                MailboxAddress? sender = message.From.Mailboxes.FirstOrDefault();
                string senderAddress = sender?.Address ?? userName;
                string? senderDisplayName = sender?.Name;
                string senderName = string.IsNullOrWhiteSpace(senderDisplayName) ? senderAddress : senderDisplayName;
                string sourceProviderId="imap:"+System.Text.Json.JsonSerializer.Serialize(new{provider=provider.Name,mailbox=StudioEmailRouting.NormalizeAddress(userName),folder=folder.FullName,uidValidity=folder.UidValidity,uid=id.Id});
                string messageId = string.IsNullOrWhiteSpace(message.MessageId) ? $"legacy-{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sourceProviderId)))}@import.local" : message.MessageId;
                string body = message.TextBody ?? message.HtmlBody ?? string.Empty;
                IReadOnlyDictionary<string, string> headers = message.Headers
                    .Where(header => !string.IsNullOrWhiteSpace(header.Field))
                    .GroupBy(header => header.Field, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Last().Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);

                InboundEmail inbound = new(
                    messageId,
                    message.InReplyTo,
                    senderName,
                    senderAddress,
                    message.To.Mailboxes.Select(x => x.Address).ToArray(),
                    message.Cc.Mailboxes.Select(x => x.Address).ToArray(),
                    headers,
                    message.Subject ?? "Imported message",
                    body,
                    message.Date == DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : message.Date.ToUniversalTime(),ProviderMessageId:sourceProviderId);

                List<InboundEmailAttachment> files=[];
                foreach (MimeEntity entity in message.Attachments)
                {
                    if (entity is MimePart part)
                    {
                        IMimeContent? content = part.Content;
                        if (content is null)
                        {
                            continue;
                        }

                        using MemoryStream stream = new();
                        await content.DecodeToAsync(stream, cancellationToken);
                        if (stream.Length > 0 && stream.Length <= attachments.MaximumBytes)
                        {
                            string contentType = part.ContentType?.MimeType ?? "application/octet-stream";
                            files.Add(new(part.FileName ?? "attachment.bin",contentType,stream.ToArray()));
                            attachmentCount++;
                        }
                    }
                    else if (entity is MessagePart attachedMessage)
                    {
                        MimeMessage? embeddedMessage = attachedMessage.Message;
                        if (embeddedMessage is null)
                        {
                            continue;
                        }

                        using MemoryStream stream = new();
                        await embeddedMessage.WriteToAsync(stream, cancellationToken);
                        if (stream.Length > 0 && stream.Length <= attachments.MaximumBytes)
                        {
                            string fileName = attachedMessage.ContentDisposition?.FileName ?? attachedMessage.ContentType?.Name ?? "attached-message.eml";
                            files.Add(new(fileName,"message/rfc822",stream.ToArray()));
                            attachmentCount++;
                        }
                    }
                }
                inbound=inbound with {Attachments=files};
                var result=await intake.ReceiveAsync(inbound,cancellationToken,new(true,workspace.Id,workspace.Name,userName,null),user:user);
                if(!result.Routed)failed++;else if(result.WasDuplicate)duplicates++;else imported++;

            }
            catch
            {
                failed++;
            }
        }

        await client.DisconnectAsync(true, cancellationToken);
        return new LegacyMailboxImportResult(provider.Name, userName, folder.FullName, authenticationMode, ids.Count, imported, duplicates, attachmentCount, failed);
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

