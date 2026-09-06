namespace Aegis.Studio.Application.EmailRouting;

public sealed record InboundEmail(
    string MessageId,
    string? ThreadId,
    string FromName,
    string FromAddress,
    IReadOnlyCollection<string> ToAddresses,
    string Subject,
    string Body,
    DateTimeOffset ReceivedAt);
