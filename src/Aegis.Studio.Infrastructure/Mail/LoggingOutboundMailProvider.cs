using Aegis.Studio.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aegis.Studio.Infrastructure.Mail;

public sealed class LoggingOutboundMailProvider : IOutboundMailProvider
{
    private readonly ILogger<LoggingOutboundMailProvider> logger;

    public LoggingOutboundMailProvider(ILogger<LoggingOutboundMailProvider> logger)
    {
        this.logger = logger;
    }

    public Task SendAsync(string fromAddress, string toAddress, string subject, string body, CancellationToken cancellationToken)
    {
        logger.LogInformation("MAIL {From} -> {To}: {Subject}", fromAddress, toAddress, subject);
        return Task.CompletedTask;
    }
}
