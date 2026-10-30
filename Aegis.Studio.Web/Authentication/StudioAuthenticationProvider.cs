namespace Aegis.Studio.Web.Authentication;

public static class StudioAuthenticationProvider
{
    public static Common.Security.Options.MockAuthenticationOptions ReadMockOptions(
        IConfigurationSection section, string environmentName, string applicationId)
    {
        var configured = Common.Security.Integration.MockAuthenticationConfiguration.Read(section, environmentName, applicationId);
        // Studio's isolated demo has no password. Preserve the shared environment
        // and server-owned identity restrictions, including immutable subject IDs.
        return new Common.Security.Options.MockAuthenticationOptions
        {
            Enabled = configured.Enabled,
            EnvironmentName = configured.EnvironmentName,
            IsolationConfirmed = configured.IsolationConfirmed,
            ApplicationId = configured.ApplicationId,
            Password = string.Empty,
            Identities = configured.Identities
        };
    }

    // Existing isolated demos keep their provider. Otherwise Entra takes precedence
    // over the AD plugin; disabling the plugin selects local credentials.
    public static string Resolve(IConfiguration configuration, bool mockEnabled, bool entraEnabled)
    {
        bool directoryEnabled = configuration.GetValue("AuthenticationPlugin:Enabled", true);
        string provider = configuration["Authentication:Provider"]?.Trim()
            ?? (mockEnabled ? "Mock" : entraEnabled ? "EntraId" : directoryEnabled ? "ActiveDirectory" : "LocalDatabase");
        bool valid = provider switch
        {
            "ActiveDirectory" => directoryEnabled,
            "LocalDatabase" => true,
            "EntraId" => entraEnabled,
            "Mock" => mockEnabled,
            _ => false
        };
        if (!valid) throw new InvalidOperationException("Authentication:Provider must name an enabled Studio authentication provider.");
        return provider;
    }

    public static bool AcceptsSource(string provider, string? requestedSource) =>
        requestedSource is null || string.Equals(provider, requestedSource, StringComparison.Ordinal);
}
