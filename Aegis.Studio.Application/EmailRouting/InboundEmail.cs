namespace Aegis.Studio.Application.EmailRouting;

public sealed record InboundEmail(
    string MessageId,
    string? ThreadId,
    string FromName,
    string FromAddress,
    IReadOnlyCollection<string> ToAddresses,
    IReadOnlyCollection<string> CcAddresses,
    IReadOnlyDictionary<string, string> InternetHeaders,
    string Subject,
    string Body,
    DateTimeOffset ReceivedAt,
    IReadOnlyCollection<InboundEmailAttachment>? Attachments = null)
{
    public IReadOnlyCollection<InboundEmailAttachment> FileAttachments => Attachments ?? Array.Empty<InboundEmailAttachment>();
}

public sealed record InboundEmailAttachment(
    string FileName,
    string ContentType,
    byte[] Content,
    bool IsInline = false);
