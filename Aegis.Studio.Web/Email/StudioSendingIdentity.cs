namespace Aegis.Studio.Web.Email;

public enum StudioSendingIdentityType
{
    Workspace,
    Self
}

public sealed record StudioSendingIdentity(
    StudioSendingIdentityType Type,
    string FromAddress,
    string? UserName);

public static class StudioSendingIdentityResolver
{
    public static StudioSendingIdentity Resolve(
        string? requestedIdentity,
        string workspaceEmail,
        string? userName,
        string? userEmail,
        bool canSendAsSelf,
        IReadOnlyCollection<string> allowedPersonalSenderDomains)
    {
        if (string.IsNullOrWhiteSpace(workspaceEmail))
            throw new InvalidOperationException("The workspace has no outbound email identity configured.");

        if (!string.Equals(requestedIdentity?.Trim(), "Self", StringComparison.OrdinalIgnoreCase))
            return new(StudioSendingIdentityType.Workspace, workspaceEmail.Trim(), userName);

        if (!canSendAsSelf)
            throw new UnauthorizedAccessException("Reply as Myself is not permitted for this workspace.");

        if (string.IsNullOrWhiteSpace(userEmail))
            throw new InvalidOperationException("Reply as Myself requires a valid Studio email address on the user profile.");

        string email = userEmail.Trim();
        int at = email.LastIndexOf('@');
        if (at <= 0 || at == email.Length - 1)
            throw new InvalidOperationException("Reply as Myself requires a valid Studio email address on the user profile.");

        string domain = email[(at + 1)..].Trim().TrimEnd('.');
        string[] allowed = allowedPersonalSenderDomains
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().TrimStart('@').TrimEnd('.'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (allowed.Length == 0 || !allowed.Contains(domain, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("The user's email address is not an approved Studio sending domain.");

        return new(StudioSendingIdentityType.Self, email, userName);
    }
}
