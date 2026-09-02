using Aegis.Studio.Application.Abstractions;

namespace Aegis.Studio.Infrastructure.Mail;

public sealed class NoOpInboundMailProvider : IInboundMailProvider
{
    public Task<IReadOnlyList<InboundMailItem>> GetUnreadAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<InboundMailItem> messages = Array.Empty<InboundMailItem>();
        return Task.FromResult(messages);
    }

    public Task MarkProcessedAsync(string providerMessageId, CancellationToken cancellationToken) => Task.CompletedTask;
}
