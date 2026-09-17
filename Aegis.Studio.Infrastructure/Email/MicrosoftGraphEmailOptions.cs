namespace Aegis.Studio.Infrastructure.Email;

public sealed class MicrosoftGraphEmailOptions
{
    public bool Enabled { get; init; }

    public string TenantId { get; init; } = string.Empty;

    public string ClientId { get; init; } = string.Empty;

    public string Mailbox { get; init; } = string.Empty;

    public int PollSeconds { get; init; } = 30;

    public int PageSize { get; init; } = 50;
}
