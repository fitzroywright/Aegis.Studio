namespace Aegis.Studio.Infrastructure.Authentication;

public sealed class ActiveDirectoryUserNameFormatter
{
    private readonly string domain;

    public ActiveDirectoryUserNameFormatter(string domain)
    {
        this.domain = domain.Trim();
    }

    public string FormatForBind(string userName)
    {
        string normalizedUserName = userName.Trim();
        if (normalizedUserName.Contains('@') || normalizedUserName.Contains('\\') || string.IsNullOrWhiteSpace(domain))
        {
            return normalizedUserName;
        }

        return $"{normalizedUserName}@{domain}";
    }

    public string FormatForAegis(string userName)
    {
        string normalizedUserName = userName.Trim();
        int slashIndex = normalizedUserName.LastIndexOf('\\');
        if (slashIndex >= 0 && slashIndex < normalizedUserName.Length - 1)
        {
            return normalizedUserName[(slashIndex + 1)..];
        }

        int atIndex = normalizedUserName.IndexOf('@');
        return atIndex > 0 ? normalizedUserName[..atIndex] : normalizedUserName;
    }
}
