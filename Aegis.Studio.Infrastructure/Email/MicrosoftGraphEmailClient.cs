using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Aegis.Studio.Application.EmailRouting;
using Common.Secrets;

namespace Aegis.Studio.Infrastructure.Email;

public sealed class MicrosoftGraphEmailClient
{
    public const string ClientSecretName = "Studio:MicrosoftGraphEmail:ClientSecret";

    private readonly MicrosoftGraphEmailOptions options;
    private readonly string? clientSecret;
    private readonly HttpClient httpClient;

    public MicrosoftGraphEmailClient(MicrosoftGraphEmailOptions options, ISecretProvider secretProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(secretProvider);

        this.options = options;
        clientSecret = secretProvider
            .GetAsync(ClientSecretName)
            .GetAwaiter()
            .GetResult();
        httpClient = new HttpClient();
    }

    public bool IsConfigured =>
        options.Enabled &&
        !string.IsNullOrWhiteSpace(options.TenantId) &&
        !string.IsNullOrWhiteSpace(options.ClientId) &&
        !string.IsNullOrWhiteSpace(clientSecret) &&
        !string.IsNullOrWhiteSpace(options.Mailbox);

    public TimeSpan PollInterval => TimeSpan.FromSeconds(Math.Max(5, options.PollSeconds));

    public async Task<MicrosoftGraphEmailVerification> VerifyConfigurationAsync(CancellationToken cancellationToken)
    {
        bool tenantConfigured = !string.IsNullOrWhiteSpace(options.TenantId);
        bool clientConfigured = !string.IsNullOrWhiteSpace(options.ClientId);
        bool secretConfigured = !string.IsNullOrWhiteSpace(clientSecret);
        bool mailboxConfigured = !string.IsNullOrWhiteSpace(options.Mailbox);

        if (!options.Enabled)
        {
            return new MicrosoftGraphEmailVerification(
                "Disabled",
                false,
                false,
                tenantConfigured,
                clientConfigured,
                secretConfigured,
                mailboxConfigured,
                null,
                "Microsoft Graph email ingestion is disabled.");
        }

        if (!tenantConfigured || !clientConfigured || !secretConfigured || !mailboxConfigured)
        {
            return new MicrosoftGraphEmailVerification(
                "Incomplete",
                false,
                false,
                tenantConfigured,
                clientConfigured,
                secretConfigured,
                mailboxConfigured,
                null,
                "One or more required Microsoft Graph settings are missing.");
        }

        string accessToken;
        try
        {
            accessToken = await GetAccessTokenAsync(cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            return new MicrosoftGraphEmailVerification(
                "Authentication failed",
                false,
                false,
                true,
                true,
                true,
                true,
                exception.StatusCode.HasValue ? (int)exception.StatusCode.Value : null,
                "Microsoft Entra ID rejected the configured tenant/client/secret credentials.");
        }
        catch (Exception)
        {
            return new MicrosoftGraphEmailVerification(
                "Authentication failed",
                false,
                false,
                true,
                true,
                true,
                true,
                null,
                "The configured Microsoft Graph credentials could not obtain an access token.");
        }

        string mailbox = Uri.EscapeDataString(options.Mailbox);
        string probeUrl = $"https://graph.microsoft.com/v1.0/users/{mailbox}/mailFolders/inbox?$select=id,displayName,totalItemCount,unreadItemCount";
        using HttpRequestMessage request = new(HttpMethod.Get, probeUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return new MicrosoftGraphEmailVerification(
                "Mailbox access failed",
                true,
                false,
                true,
                true,
                true,
                true,
                (int)response.StatusCode,
                $"Microsoft Graph returned {(int)response.StatusCode} {response.ReasonPhrase} while opening the configured Inbox.");
        }

        GraphMailFolderProbe? folder = await response.Content.ReadFromJsonAsync<GraphMailFolderProbe>(cancellationToken: cancellationToken);
        return new MicrosoftGraphEmailVerification(
            "Verified",
            true,
            true,
            true,
            true,
            true,
            true,
            (int)response.StatusCode,
            folder is null
                ? "Credentials are valid and the configured mailbox Inbox is accessible."
                : $"Credentials are valid. Inbox is accessible with {folder.TotalItemCount} total item(s) and {folder.UnreadItemCount} unread item(s).");
    }

    public async Task<GraphInboxDeltaBatch> GetInboxChangesAsync(
        string? deltaLink,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return new GraphInboxDeltaBatch(Array.Empty<InboundEmail>(), deltaLink);
        }

        string accessToken = await GetAccessTokenAsync(cancellationToken);
        string? nextUrl = string.IsNullOrWhiteSpace(deltaLink)
            ? BuildInitialDeltaUrl()
            : deltaLink;
        string? completedDeltaLink = deltaLink;
        List<InboundEmail> messages = new();

        while (!string.IsNullOrWhiteSpace(nextUrl))
        {
            using HttpRequestMessage request = new(HttpMethod.Get, nextUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.TryAddWithoutValidation("Prefer", "outlook.body-content-type=\"text\"");

            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Gone && !string.IsNullOrWhiteSpace(deltaLink))
            {
                throw new GraphDeltaTokenExpiredException();
            }

            response.EnsureSuccessStatusCode();

            GraphMessageDeltaPage? page = await response.Content.ReadFromJsonAsync<GraphMessageDeltaPage>(
                cancellationToken: cancellationToken);
            if (page is null)
            {
                throw new InvalidOperationException("Microsoft Graph returned an empty delta response.");
            }

            foreach (GraphMessage message in page.Value.Where(message => message.Removed is null).Where(IsUsable))
            {
                IReadOnlyCollection<InboundEmailAttachment> attachments = message.HasAttachments
                    ? await GetFileAttachmentsAsync(accessToken, message.Id!, cancellationToken)
                    : Array.Empty<InboundEmailAttachment>();
                messages.Add(ToInboundEmail(message, attachments));
            }

            if (!string.IsNullOrWhiteSpace(page.NextLink))
            {
                nextUrl = page.NextLink;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(page.DeltaLink))
            {
                completedDeltaLink = page.DeltaLink;
            }

            nextUrl = null;
        }

        return new GraphInboxDeltaBatch(messages, completedDeltaLink);
    }

    public async Task SendEmailAsync(
        string fromAddress,
        string toAddress,
        string subject,
        string body,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Microsoft Graph email is not configured.");
        }

        if (string.IsNullOrWhiteSpace(fromAddress))
        {
            throw new InvalidOperationException("An explicit sending mailbox is required.");
        }

        string accessToken = await GetAccessTokenAsync(cancellationToken);
        string url = BuildSendMailUrl(fromAddress);

        GraphSendMailRequest payload = new(
            new GraphSendMessage(
                subject,
                new GraphBody("Text", body),
                new GraphRecipient { EmailAddress = new GraphEmailAddress { Address = fromAddress } },
                new List<GraphRecipient>
                {
                    new() { EmailAddress = new GraphEmailAddress { Address = toAddress } }
                }),
            false);

        using HttpRequestMessage request = new(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(payload);

        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public static string BuildSendMailUrl(string fromAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromAddress);
        string sendingMailbox = Uri.EscapeDataString(fromAddress.Trim());
        return $"https://graph.microsoft.com/v1.0/users/{sendingMailbox}/sendMail";
    }

    private async Task<IReadOnlyCollection<InboundEmailAttachment>> GetFileAttachmentsAsync(
        string accessToken,
        string messageId,
        CancellationToken cancellationToken)
    {
        string mailbox = Uri.EscapeDataString(options.Mailbox);
        string escapedMessageId = Uri.EscapeDataString(messageId);
        string? nextUrl = $"https://graph.microsoft.com/v1.0/users/{mailbox}/messages/{escapedMessageId}/attachments?$select=id,name,contentType,isInline,@odata.type,contentBytes";
        List<InboundEmailAttachment> attachments = [];

        while (!string.IsNullOrWhiteSpace(nextUrl))
        {
            using HttpRequestMessage request = new(HttpMethod.Get, nextUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            GraphAttachmentPage? page = await response.Content.ReadFromJsonAsync<GraphAttachmentPage>(cancellationToken: cancellationToken);
            if (page is null)
            {
                throw new InvalidOperationException("Microsoft Graph returned an empty attachment response.");
            }

            foreach (GraphAttachment attachment in page.Value)
            {
                if (!string.Equals(attachment.ODataType, "#microsoft.graph.fileAttachment", StringComparison.OrdinalIgnoreCase) ||
                    attachment.IsInline ||
                    string.IsNullOrWhiteSpace(attachment.ContentBytes))
                {
                    continue;
                }

                byte[] content;
                try
                {
                    content = Convert.FromBase64String(attachment.ContentBytes);
                }
                catch (FormatException exception)
                {
                    throw new InvalidOperationException($"Microsoft Graph returned invalid base64 content for attachment '{attachment.Name}'.", exception);
                }

                if (content.Length == 0)
                {
                    continue;
                }

                attachments.Add(new InboundEmailAttachment(
                    string.IsNullOrWhiteSpace(attachment.Name) ? "attachment.bin" : attachment.Name,
                    string.IsNullOrWhiteSpace(attachment.ContentType) ? "application/octet-stream" : attachment.ContentType,
                    content,
                    false));
            }

            nextUrl = page.NextLink;
        }

        return attachments;
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        string tenantId = Uri.EscapeDataString(options.TenantId);
        string url = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";
        using FormUrlEncodedContent content = new(new Dictionary<string, string>
        {
            ["client_id"] = options.ClientId,
            ["client_secret"] = clientSecret!,
            ["scope"] = "https://graph.microsoft.com/.default",
            ["grant_type"] = "client_credentials"
        });

        using HttpResponseMessage response = await httpClient.PostAsync(url, content, cancellationToken);
        response.EnsureSuccessStatusCode();

        GraphTokenResponse? token = await response.Content.ReadFromJsonAsync<GraphTokenResponse>(cancellationToken: cancellationToken);
        string? accessToken = token?.AccessToken;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("Microsoft Graph did not return an access token.");
        }

        return accessToken;
    }

    private string BuildInitialDeltaUrl()
    {
        int pageSize = Math.Clamp(options.PageSize, 1, 100);
        string mailbox = Uri.EscapeDataString(options.Mailbox);
        return $"https://graph.microsoft.com/v1.0/users/{mailbox}/mailFolders/inbox/messages/delta" +
            $"?$select=id,conversationId,subject,receivedDateTime,from,toRecipients,ccRecipients,body,internetMessageHeaders,hasAttachments" +
            $"&$top={pageSize}";
    }

    private static bool IsUsable(GraphMessage message)
    {
        return !string.IsNullOrWhiteSpace(message.Id) &&
            !string.IsNullOrWhiteSpace(message.From?.EmailAddress?.Address) &&
            (message.ToRecipients.Count > 0 || message.CcRecipients.Count > 0) &&
            !string.IsNullOrWhiteSpace(message.Subject) &&
            !string.IsNullOrWhiteSpace(message.Body?.Content);
    }

    private static InboundEmail ToInboundEmail(
        GraphMessage message,
        IReadOnlyCollection<InboundEmailAttachment> attachments)
    {
        GraphEmailAddress sender = message.From!.EmailAddress!;
        IReadOnlyCollection<string> toRecipients = ExtractAddresses(message.ToRecipients);
        IReadOnlyCollection<string> ccRecipients = ExtractAddresses(message.CcRecipients);
        IReadOnlyDictionary<string, string> headers = message.InternetMessageHeaders
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .GroupBy(x => x.Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Last().Value ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);

        string senderAddress = sender.Address!;
        string senderName = string.IsNullOrWhiteSpace(sender.Name) ? senderAddress : sender.Name!;

        return new InboundEmail(
            message.Id!,
            message.ConversationId,
            senderName,
            senderAddress,
            toRecipients,
            ccRecipients,
            headers,
            message.Subject!,
            message.Body!.Content,
            message.ReceivedDateTime ?? DateTimeOffset.UtcNow,
            attachments);
    }

    private static IReadOnlyCollection<string> ExtractAddresses(IEnumerable<GraphRecipient> recipients)
    {
        return recipients
            .Select(x => x.EmailAddress?.Address)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToList();
    }

    private sealed class GraphMailFolderProbe
    {
        [JsonPropertyName("totalItemCount")]
        public int TotalItemCount { get; init; }

        [JsonPropertyName("unreadItemCount")]
        public int UnreadItemCount { get; init; }
    }

    private sealed class GraphTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }
    }

    private sealed class GraphMessageDeltaPage
    {
        [JsonPropertyName("value")]
        public List<GraphMessage> Value { get; init; } = new();

        [JsonPropertyName("@odata.nextLink")]
        public string? NextLink { get; init; }

        [JsonPropertyName("@odata.deltaLink")]
        public string? DeltaLink { get; init; }
    }

    private sealed class GraphAttachmentPage
    {
        [JsonPropertyName("value")]
        public List<GraphAttachment> Value { get; init; } = new();

        [JsonPropertyName("@odata.nextLink")]
        public string? NextLink { get; init; }
    }

    private sealed class GraphAttachment
    {
        [JsonPropertyName("@odata.type")]
        public string? ODataType { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("contentType")]
        public string? ContentType { get; init; }

        [JsonPropertyName("isInline")]
        public bool IsInline { get; init; }

        [JsonPropertyName("contentBytes")]
        public string? ContentBytes { get; init; }
    }

    private sealed class GraphMessage
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("conversationId")]
        public string? ConversationId { get; init; }

        [JsonPropertyName("subject")]
        public string? Subject { get; init; }

        [JsonPropertyName("receivedDateTime")]
        public DateTimeOffset? ReceivedDateTime { get; init; }

        [JsonPropertyName("from")]
        public GraphRecipient? From { get; init; }

        [JsonPropertyName("toRecipients")]
        public List<GraphRecipient> ToRecipients { get; init; } = new();

        [JsonPropertyName("ccRecipients")]
        public List<GraphRecipient> CcRecipients { get; init; } = new();

        [JsonPropertyName("internetMessageHeaders")]
        public List<GraphInternetMessageHeader> InternetMessageHeaders { get; init; } = new();

        [JsonPropertyName("body")]
        public GraphBody? Body { get; init; }

        [JsonPropertyName("hasAttachments")]
        public bool HasAttachments { get; init; }

        [JsonPropertyName("@removed")]
        public object? Removed { get; init; }
    }

    private sealed class GraphInternetMessageHeader
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("value")]
        public string? Value { get; init; }
    }

    private sealed record GraphSendMailRequest(
        [property: JsonPropertyName("message")] GraphSendMessage Message,
        [property: JsonPropertyName("saveToSentItems")] bool SaveToSentItems);

    private sealed record GraphSendMessage(
        [property: JsonPropertyName("subject")] string Subject,
        [property: JsonPropertyName("body")] GraphBody Body,
        [property: JsonPropertyName("from")] GraphRecipient From,
        [property: JsonPropertyName("toRecipients")] List<GraphRecipient> ToRecipients);

    private sealed record GraphBody(
        [property: JsonPropertyName("contentType")] string ContentType,
        [property: JsonPropertyName("content")] string Content);

    private sealed class GraphRecipient
    {
        [JsonPropertyName("emailAddress")]
        public GraphEmailAddress? EmailAddress { get; init; }
    }

    private sealed class GraphEmailAddress
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("address")]
        public string? Address { get; init; }
    }
}

public sealed record GraphInboxDeltaBatch(
    IReadOnlyList<InboundEmail> Messages,
    string? DeltaLink);

public sealed class GraphDeltaTokenExpiredException : Exception
{
    public GraphDeltaTokenExpiredException()
        : base("The Microsoft Graph delta token expired and a fresh synchronization is required.")
    {
    }
}


public sealed record MicrosoftGraphEmailVerification(
    string State,
    bool AuthenticationSucceeded,
    bool MailboxAccessible,
    bool TenantConfigured,
    bool ClientConfigured,
    bool ClientSecretConfigured,
    bool MailboxConfigured,
    int? GraphStatusCode,
    string Detail);
