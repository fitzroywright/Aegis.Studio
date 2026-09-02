namespace Aegis.Studio.Application.Abstractions;

public interface IOutboundMailProvider
{
    Task SendAsync(string fromAddress, string toAddress, string subject, string body, CancellationToken cancellationToken);
}
