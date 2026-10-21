using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Common.Messaging;
using Common.Messaging.Channels.MicrosoftGraph;

namespace Aegis.Studio.Web.Email;

public sealed class StudioDelegatedGraphChannel(HttpClient http, StudioDelegatedGraphSession session, IConfiguration configuration) : IExternalMessageChannel
{
    public MessageChannel Channel => MessageChannel.MsEmail;

    public async Task SendAsync(MessageRecipient recipient, MessageRequest request, Guid notificationId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(recipient.Email)) throw new InvalidOperationException("An email recipient is required.");
        if (request.Attachments.Sum(a => (long)a.Content.Length) > 2_500_000)
            throw new InvalidOperationException("Email attachments exceed the supported 2.5 MB total.");
        string mime = MicrosoftGraphMimeEnvelope.Build(recipient, request, notificationId, StudioGraphMailbox.Resolve(configuration));
        if (mime.Length > 4_000_000) throw new InvalidOperationException("Graph MIME payload exceeds 4 MB.");
        // Token acquisition is before submission: a reconnect failure cannot indicate a sent mail.
        string bearer = await session.AccessTokenAsync(cancellationToken);
        using var message = new HttpRequestMessage(HttpMethod.Post, "https://graph.microsoft.com/v1.0/me/sendMail");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        message.Content = new StringContent(mime, Encoding.ASCII, "text/plain");
        HttpResponseMessage submitted;
        try { submitted = await http.SendAsync(message, cancellationToken); }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        { throw new ExternalDeliveryOutcomeUnknownException(ex); }
        using var response = submitted;
        if (response.StatusCode == HttpStatusCode.Accepted) return;
        if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout || response.IsSuccessStatusCode)
            throw new ExternalDeliveryOutcomeUnknownException(new HttpRequestException("Delegated Graph submission outcome requires reconciliation.", null, response.StatusCode));
        // Do not retry a 401 automatically: an operator must reconnect and explicitly retry.
        throw new HttpRequestException($"Delegated Graph send rejected. HTTP {(int)response.StatusCode}; RequestId={RequestId(response)}.", null, response.StatusCode);
    }

    private static string RequestId(HttpResponseMessage response) => response.Headers.TryGetValues("request-id", out var values)
        && Guid.TryParse(values.FirstOrDefault(), out var id) ? id.ToString("D") : "Unavailable";
}
