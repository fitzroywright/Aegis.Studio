namespace Aegis.Studio.Web.Authentication;

public sealed record MockAuthenticationIdentity
(
      string UserName
    , string DisplayName
    , string? Email
    , string? Department
    , string? Title
    , IReadOnlyCollection<string> GroupNames
);

public static class MockAuthenticationPolicy
{
    public static bool IsEnabled(IConfiguration configuration, string environmentName)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase)
            && configuration.GetValue("MockAuthentication:Enabled", false);
    }

    public static MockAuthenticationIdentity CreateIdentity(
        IConfiguration configuration,
        string requestedUserName,
        IReadOnlyCollection<string> administratorGroups)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(administratorGroups);

        string userName = configuration["MockAuthentication:UserName"]?.Trim()
            ?? requestedUserName.Trim();

        if (string.IsNullOrWhiteSpace(userName))
        {
            userName = "studio.admin";
        }

        string displayName = configuration["MockAuthentication:DisplayName"]?.Trim()
            ?? userName;

        string[] configuredGroups = configuration
            .GetSection("MockAuthentication:Groups")
            .Get<string[]>() ?? [];

        string[] groups = configuredGroups
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Select(group => group.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (groups.Length == 0)
        {
            groups = administratorGroups
                .Where(group => !string.IsNullOrWhiteSpace(group))
                .Select(group => group.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return new MockAuthenticationIdentity(
            userName,
            displayName,
            NullIfWhiteSpace(configuration["MockAuthentication:Email"]),
            NullIfWhiteSpace(configuration["MockAuthentication:Department"]),
            NullIfWhiteSpace(configuration["MockAuthentication:Title"]),
            groups);
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
