namespace Aegis.Studio.Application.Abstractions;

public interface IInboundMailProvider
{
    Task<IReadOnlyList<InboundMailItem>> GetUnreadAsync(CancellationToken cancellationToken);
    Task MarkProcessedAsync(string providerMessageId, CancellationToken cancellationToken);
}

public sealed record InboundMailItem(string ProviderMessageId, string FromAddress, string OriginalRecipientAddress, string Subject, string Body, string? InternetMessageId);
