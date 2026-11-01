using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Aegis.Studio.Web.Operations;

public sealed record StudioDeploymentInfo(string Version, DateTimeOffset? DeployedAtUtc)
{
    public static StudioDeploymentInfo Read(IConfiguration configuration, string contentRoot)
    {
        string? version = configuration["Deployment:Version"];
        string? date = configuration["Deployment:DeployedAtUtc"];
        try
        {
            string path = Path.Combine(contentRoot, "studio-deployment.json");
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                if (string.IsNullOrWhiteSpace(version) && root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String) version = v.GetString();
                if (string.IsNullOrWhiteSpace(date) && root.TryGetProperty("deployedAtUtc", out var d) && d.ValueKind == JsonValueKind.String) date = d.GetString();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // Older installations may have no metadata; never substitute process start time.
        }
        version = string.IsNullOrWhiteSpace(version)
            ? typeof(StudioDeploymentInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "Unknown"
            : version.Trim();
        DateTimeOffset? deployed = DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed.ToUniversalTime() : null;
        return new(version, deployed);
    }
}
