namespace Aegis.Studio.Web.Email;

// Transport mailbox and visible sending identity are deliberately independent.
// Never fall back to a workspace/person distribution list for Graph /users calls.
public static class StudioGraphMailbox
{
    public static string Resolve(IConfiguration configuration)
    {
        string? mailbox = configuration["Messaging:MicrosoftGraph:Email:SenderUpn"];
        if (string.IsNullOrWhiteSpace(mailbox))
            throw new InvalidOperationException("The Microsoft Graph hub mailbox (Messaging:MicrosoftGraph:Email:SenderUpn) is required.");
        return mailbox.Trim();
    }
}
