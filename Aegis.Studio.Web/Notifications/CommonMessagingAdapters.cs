using System.Net.Http.Json;
using Aegis.Studio.Web.Profiles;
using Common.Messaging;
using Common.Secrets;

namespace Aegis.Studio.Web.Notifications;

internal sealed class StudioMessageRecipientDirectory : IMessageRecipientDirectory
{
    private readonly UserProfileStore profiles;

    public StudioMessageRecipientDirectory(UserProfileStore profiles)
    {
        this.profiles = profiles;
    }

    public Task<IReadOnlyList<MessageRecipient>> ResolveAsync(
        IReadOnlyCollection<string> recipientIds,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        HashSet<string> requested = recipientIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<MessageRecipient> recipients = profiles
            .GetAll()
            .Where(profile => requested.Contains(profile.UserName) || requested.Contains(profile.Id.ToString("D")))
            .Select(profile => new MessageRecipient(
                profile.UserName,
                profile.DisplayName,
                profile.Email,
                Mobile: profile.Messaging.WhatsAppMobile,
                PreferredChannels: ToCommonChannels(profile.Messaging)))
            .ToList();

        return Task.FromResult(recipients);
    }

    private static MessageChannel ToCommonChannels(UserMessagingPreferences preferences)
    {
        MessageChannel channels = MessageChannel.None;
        if (preferences.InAppEnabled) channels |= MessageChannel.InApp;
        if (preferences.EmailEnabled) channels |= MessageChannel.MsEmail;
        if (preferences.SlackEnabled) channels |= MessageChannel.Slack;
        if (preferences.TeamsEnabled) channels |= MessageChannel.MsTeams;
        if (preferences.WhatsAppEnabled) channels |= MessageChannel.WhatsApp;
        return channels;
    }
}

internal sealed class StudioWebhookMessageChannel : IExternalMessageChannel, IDisposable
{
    private readonly ISecretProvider secrets;
    private readonly string secretName;
    private readonly Func<MessageRequest, object> payloadFactory;
    private readonly HttpClient httpClient = new();

    public StudioWebhookMessageChannel(
        MessageChannel channel,
        ISecretProvider secrets,
        string secretName,
        Func<MessageRequest, object> payloadFactory)
    {
        Channel = channel;
        this.secrets = secrets;
        this.secretName = secretName;
        this.payloadFactory = payloadFactory;
    }

    public MessageChannel Channel { get; }

    public async Task SendAsync(
        MessageRecipient recipient,
        MessageRequest request,
        Guid notificationId,
        CancellationToken cancellationToken = default)
    {
        string? webhookUrl = await secrets.GetAsync(secretName, cancellationToken);
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            throw new StudioMessageConfigurationException($"{Channel} webhook is not configured.");
        }

        using HttpResponseMessage response = await httpClient.PostAsJsonAsync(
            webhookUrl,
            payloadFactory(request),
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose()
    {
        httpClient.Dispose();
    }
}

internal sealed class StudioMessageConfigurationException : InvalidOperationException
{
    public StudioMessageConfigurationException(string message)
        : base(message)
    {
    }
}
