using Microsoft.Extensions.Configuration;

namespace Aegis.Studio.Infrastructure.Authentication;

public sealed class ActiveDirectoryOptions
{
    public string Server { get; init; } = string.Empty;
    public int Port { get; init; } = 636;
    public string Domain { get; init; } = string.Empty;
    public bool UseSsl { get; init; } = true;
    public bool AllowInsecureLdap { get; init; }

    public static ActiveDirectoryOptions Load(IConfiguration configuration)
    {
        IConfigurationSection section = configuration.GetSection("Authentication:ActiveDirectory");
        return new ActiveDirectoryOptions
        {
            Server = section["Server"] ?? string.Empty,
            Port = int.TryParse(section["Port"], out int port) ? port : 636,
            Domain = section["Domain"] ?? string.Empty,
            UseSsl = !bool.TryParse(section["UseSsl"], out bool useSsl) || useSsl,
            AllowInsecureLdap = bool.TryParse(section["AllowInsecureLdap"], out bool allowInsecureLdap) && allowInsecureLdap
        };
    }
}
