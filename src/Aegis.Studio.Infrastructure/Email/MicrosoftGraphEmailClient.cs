using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Aegis.Studio.Application.EmailRouting;

namespace Aegis.Studio.Infrastructure.Email;

public sealed class MicrosoftGraphEmailClient
{
    private readonly MicrosoftGraphEmailOptions options;
    private readonly HttpClient httpClient;

    public MicrosoftGraphEmailClient(MicrosoftGraphEmailOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        this.options = options;
        httpClient = new HttpClient();
    }

    public bool IsConfigured => options.IsConfigured;

    public TimeSpan PollInterval => TimeSpan.FromSeconds(Math.Max(5, options.PollSeconds));

    public async Task<IReadOnlyList<InboundEmail>> GetInboxMessagesAsync(CancellationToken cancellationToken)
    {
        if (!options.IsConfigured)
        {
            return Array.Empty<InboundEmail>();
        }

        string accessToken = await GetAccessTokenAsync(cancellationToken);
        using HttpRequestMessage request = new(HttpMethod.Get, BuildMessagesUrl());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        GraphMessagePage? page = await response.Content.ReadFromJsonAsync<GraphMessagePage>(cancellationToken: cancellationToken);
        if (page?.Value is null)
        {
            return Array.Empty<InboundEmail>();
        }

        return page.Value
            .Where(IsUsable)
            .Select(ToInboundEmail)
            .ToList();
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        string tenantId = Uri.EscapeDataString(options.TenantId);
        string url = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";
        using FormUrlEncodedContent content = new(new Dictionary<string, string>
        {
            ["client_id"] = options.ClientId,
            ["client_secret"] = options.ClientSecret,
            ["scope"] = "https://graph.microsoft.com/.default",
            ["grant_type"] = "client_credentials"
        });

        using HttpResponseMessage response = await httpClient.PostAsync(url, content, cancellationToken);
        response.EnsureSuccessStatusCode();

        GraphTokenResponse? token = await response.Content.ReadFromJsonAsync<GraphTokenResponse>(cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(token?.AccessToken))
        {
            throw new InvalidOperationException("Microsoft Graph did not return an access token.");
        }

        return token.AccessToken;
    }

    private string BuildMessagesUrl()
    {
        int pageSize = Math.Clamp(options.PageSize, 1, 100);
        string mailbox = Uri.EscapeDataString(options.Mailbox);
        return $"https://graph.microsoft.com/v1.0/users/{mailbox}/mailFolders/inbox/messages" +
            $"?$select=id,conversationId,subject,receivedDateTime,from,toRecipients,bodyPreview" +
            $"&$orderby=receivedDateTime asc&$top={pageSize}";
    }

    private static bool IsUsable(GraphMessage message)
    {
        return !string.IsNullOrWhiteSpace(message.Id) &&
            !string.IsNullOrWhiteSpace(message.From?.EmailAddress?.Address) &&
            message.ToRecipients.Count > 0 &&
            !string.IsNullOrWhiteSpace(message.Subject) &&
            !string.IsNullOrWhiteSpace(message.BodyPreview);
    }

    private static InboundEmail ToInboundEmail(GraphMessage message)
    {
        GraphEmailAddress sender = message.From!.EmailAddress!;
        IReadOnlyCollection<string> recipients = message.ToRecipients
            .Select(x => x.EmailAddress?.Address)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToList();

        string senderAddress = sender.Address!;
        string senderName = string.IsNullOrWhiteSpace(sender.Name) ? senderAddress : sender.Name;

        return new InboundEmail(
            message.Id!,
            message.ConversationId,
            senderName,
            senderAddress,
            recipients,
            message.Subject!,
            message.BodyPreview!,
            message.ReceivedDateTime ?? DateTimeOffset.UtcNow);
    }

    private sealed class GraphTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }
    }

    private sealed class GraphMessagePage
    {
        [JsonPropertyName("value")]
        public List<GraphMessage> Value { get; init; } = new();
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

        [JsonPropertyName("bodyPreview")]
        public string? BodyPreview { get; init; }
    }

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
