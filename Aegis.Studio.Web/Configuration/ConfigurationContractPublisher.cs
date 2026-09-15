using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Common.Secrets;

namespace Aegis.Studio.Web.Configuration;

public sealed class ConfigurationContractPublisher(
    IConfiguration configuration,
    IWebHostEnvironment environment,
    ISecretProvider secrets,
    ILogger<ConfigurationContractPublisher> logger) : BackgroundService
{
    private const string ApplicationId = "Aegis.Studio";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? baseUrl = First(configuration["Aegis:Configuration:Url"], configuration["AegisConfiguration:BaseUrl"]);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            logger.LogInformation("Aegis.Configuration publication is disabled because no Configuration URL is configured.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            if (await TryPublishAsync(baseUrl, stoppingToken)) return;
            try { await Task.Delay(RetryDelay, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    private async Task<bool> TryPublishAsync(string baseUrl, CancellationToken cancellationToken)
    {
        try
        {
            string? registrationKey = await secrets.GetAsync($"configuration/registration/{ApplicationId}", cancellationToken);
            if (string.IsNullOrWhiteSpace(registrationKey))
            {
                logger.LogWarning("Aegis.Configuration registration credential is unavailable; Studio remains operational and will retry publication.");
                return false;
            }

            string version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "1.0.0";
            string? siteId = First(configuration["Deployment:SiteId"], configuration["Site:Id"]);
            string instanceId = First(configuration["Service:Identity"], Environment.MachineName) ?? Environment.MachineName;

            bool databaseSecret = HasValue(await secrets.GetAsync("ConnectionStrings:Studio", cancellationToken));
            bool graphClientSecret = HasValue(await secrets.GetAsync("Studio:MicrosoftGraphEmail:ClientSecret", cancellationToken));
            bool slackSecret = HasValue(await secrets.GetAsync("Notifications:SlackWebhookUrl", cancellationToken));
            bool teamsSecret = HasValue(await secrets.GetAsync("Notifications:TeamsWebhookUrl", cancellationToken));

            object[] settings =
            [
                Config("configuration-url", "Aegis:Configuration:Url", "System Configuration URL", false, "Aegis.Configuration endpoint.", "http://localhost:5101"),
                Config("diagnostics-url", "Aegis:Diagnostics:Url", "System Diagnostics URL", false, "Aegis.Diagnostics endpoint.", "http://localhost:5102"),
                Config("site-id", "Deployment:SiteId", "Site ID", false, "Deployment site identifier."),
                Config("service-identity", "Service:Identity", "Service Identity", false, "Stable runtime instance identifier.", Environment.MachineName),
                Secret("database", "ConnectionStrings:Studio", "Studio Database Connection", true, "PostgreSQL connection string used by Npgsql.", databaseSecret, "ConnectionStrings:Studio"),
                Config("graph-enabled", "MicrosoftGraphEmail:Enabled", "Microsoft Graph Email Enabled", false, "Enable Microsoft Graph email collection.", "true"),
                Config("graph-tenant", "MicrosoftGraphEmail:TenantId", "Microsoft Graph Tenant ID", true, "Microsoft Entra tenant used by Graph email."),
                Config("graph-client", "MicrosoftGraphEmail:ClientId", "Microsoft Graph Client ID", true, "Application client ID used by Graph email."),
                Secret("graph-client-secret", "MicrosoftGraphEmail:ClientSecret", "Microsoft Graph Client Secret", true, "Client credential used by Microsoft Graph.", graphClientSecret, "Studio:MicrosoftGraphEmail:ClientSecret", "MicrosoftGraphEmail:Enabled=true"),
                Config("graph-mailbox", "MicrosoftGraphEmail:Mailbox", "Graph Mailbox", true, "Collection mailbox monitored by Studio."),
                Config("graph-poll", "MicrosoftGraphEmail:PollSeconds", "Graph Poll Interval", false, "Polling interval for inbound Graph mail.", "30"),
                Config("graph-page", "MicrosoftGraphEmail:PageSize", "Graph Page Size", false, "Maximum Graph messages requested per page.", "50"),
                Secret("slack-webhook", "Notifications:SlackWebhookUrl", "Slack Webhook", false, "Optional Slack notification webhook.", slackSecret, "Notifications:SlackWebhookUrl"),
                Secret("teams-webhook", "Notifications:TeamsWebhookUrl", "Teams Webhook", false, "Optional Teams notification webhook.", teamsSecret, "Notifications:TeamsWebhookUrl"),
                Config("attachments-storage", "Storage:AttachmentsPath", "Attachments Path", true, "Filesystem path used for Studio attachments."),
                Config("media-archive", "Storage:MediaArchivePath", "Media Archive Path", false, "Filesystem path used for archived media."),
                Config("max-attachment", "Storage:MaxAttachmentMegabytes", "Maximum Attachment Size", false, "Maximum attachment size in MiB.", "100"),
                Config("max-gallery-image", "Storage:MaxGalleryImageMegabytes", "Maximum Gallery Image Size", false, "Maximum gallery image size in MiB.", "25"),
                Config("require-https", "Security:RequireHttps", "Require HTTPS", false, "Require HTTPS in production.", "true"),
                Config("same-origin", "Security:RequireSameOriginWrites", "Require Same-Origin Writes", false, "Protect mutating routes from cross-origin requests.", "true"),
                Config("max-request", "Security:MaxRequestMegabytes", "Maximum Request Size", false, "Maximum normal request size in MiB.", "8"),
                Config("max-multipart", "Security:MaxMultipartMegabytes", "Maximum Multipart Size", false, "Maximum multipart request size in MiB.", "300"),
                Config("public-originals", "Galleries:AllowPublicOriginalDownloads", "Allow Public Original Downloads", false, "Permit original gallery file downloads.", "false"),
                Secret("secret-zero", "Secret0", "OpenBao Secret Zero", true, "Bootstrap credential/material used to establish OpenBao access. Value is never published.", SecretZeroDetected(), "OpenBao bootstrap")
            ];

            List<object> dependencies =
            [
                Dependency("configuration", "Aegis.Configuration", false, "Feature", "Publishes Studio operational contracts.", "Studio continues running but Configuration loses visibility.", "HTTPS/JSON", null, baseUrl, await ReachableAsync(baseUrl, cancellationToken)),
                Dependency("diagnostics", "Aegis.Diagnostics", false, "Feature", "Central diagnostics visibility.", "Studio continues with local diagnostics only.", "HTTPS/JSON", null, configuration["Aegis:Diagnostics:Url"], await ReachableAsync(configuration["Aegis:Diagnostics:Url"], cancellationToken)),
                Dependency("database", "Studio PostgreSQL Database", true, "Startup", "Stores Studio workspaces, jobs, bookings, messages and operational state.", "Application cannot initialize its persistent stores.", "Npgsql", "PostgreSQL", null, null),
                Dependency("graph", "Microsoft Graph", configuration.GetValue("MicrosoftGraphEmail:Enabled", true), "Feature", "Inbound/outbound email integration for Studio workspaces.", "Core Studio remains available; email routing and collection are degraded.", "Microsoft Graph REST", "Microsoft 365", "https://graph.microsoft.com", await ReachableAsync("https://graph.microsoft.com", cancellationToken)),
                Dependency("active-directory", "Active Directory", true, "Startup", "Authenticates Studio users through Common.Security.", "Interactive sign-in is unavailable.", "LDAP/LDAPS via Common.Security", "Active Directory", null, null),
                Dependency("filesystem", "Studio File Storage", true, "Feature", "Stores attachments and media archive content.", "Database features remain available but affected file operations fail.", "Filesystem", "Operating System Filesystem", null, null),
                Dependency("slack", "Slack", false, "Feature", "Optional workspace notifications.", "Slack notifications are skipped.", "Webhook HTTPS", "Slack", null, null),
                Dependency("teams", "Microsoft Teams", false, "Feature", "Optional workspace notifications.", "Teams notifications are skipped.", "Webhook HTTPS", "Microsoft Teams", null, null)
            ];

            object detectedEnvironment = await DetectEnvironmentAsync(cancellationToken);
            object registration = new
            {
                configuration = new
                {
                    applicationId = ApplicationId,
                    displayName = "Aegis Studio",
                    version,
                    siteId,
                    instanceId,
                    description = "Studio workspace, booking, gallery, email-routing and production workflow application.",
                    requirements = settings
                },
                dependencies = new { applicationId = ApplicationId, version, siteId, instanceId, dependencies },
                environment = new { applicationId = ApplicationId, version, siteId, instanceId, environment = detectedEnvironment }
            };

            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(10) };
            using HttpRequestMessage request = new(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/registrations/register");
            request.Headers.TryAddWithoutValidation("X-Aegis-Registration-Key", registrationKey);
            request.Content = new StringContent(JsonSerializer.Serialize(registration), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Aegis.Configuration rejected Studio operational registration with HTTP {StatusCode}; Studio remains operational and will retry.", (int)response.StatusCode);
                return false;
            }
            logger.LogInformation("Aegis.Studio configuration, dependency and detected-environment contracts registered with Aegis.Configuration.");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to publish Aegis.Studio operational contracts; Studio remains operational and will retry publication.");
            return false;
        }
    }

    private object Config(string id, string key, string name, bool required, string purpose, string? defaultValue = null, string? condition = null)
    {
        string envKey = key.Replace(":", "__", StringComparison.Ordinal);
        string? envValue = Environment.GetEnvironmentVariable(envKey);
        string? configuredValue = configuration[key];
        string? value = First(envValue, configuredValue, defaultValue);
        string source = HasValue(envValue) ? $"Environment Variable ({envKey})" : HasValue(configuredValue) ? "Configuration Provider" : defaultValue is not null ? "Code Default" : "Unresolved";
        return new
        {
            id, displayName = name, kind = "Configuration", required, purpose, configurationKey = key,
            isConfigured = HasValue(value), safeDisplayValue = value, sensitive = false, conditionalOn = condition,
            allowedSources = new[] { "Environment Variable", $"appsettings.{environment.EnvironmentName}.json", "appsettings.json", "Code Default" },
            resolutionOrder = new[] { "Environment Variable", $"appsettings.{environment.EnvironmentName}.json", "appsettings.json", "Code Default" },
            effectiveSource = source, hasDefault = defaultValue is not null, defaultValue, redacted = false
        };
    }

    private object Secret(string id, string key, string name, bool required, string purpose, bool configured, string secretPath, string? condition = null) => new
    {
        id, displayName = name, kind = "Secret", required, purpose, configurationKey = key,
        isConfigured = configured, safeDisplayValue = (string?)null, sensitive = true, conditionalOn = condition,
        allowedSources = new[] { "OpenBao", "approved Environment Variable override", "Secret0 bootstrap" },
        resolutionOrder = new[] { "OpenBao", "approved Environment Variable override", "no plaintext fallback" },
        effectiveSource = configured ? "OpenBao/Common.Secrets" : "Unresolved", hasDefault = false,
        defaultValue = (string?)null, secretPath, redacted = true
    };

    private static object Dependency(string id, string name, bool required, string criticality, string purpose, string degraded, string @interface, string? engine, string? endpoint, bool? reachable) => new
    {
        id, displayName = name, required, criticality, purpose, degradedBehavior = degraded, @interface, engine, endpoint, reachable,
        evidence = "Explicitly declared from application code; no connection-string inference."
    };

    private async Task<object> DetectEnvironmentAsync(CancellationToken cancellationToken)
    {
        List<object> disks = [];
        List<object> paths = [];
        foreach (string path in RelevantPaths())
        {
            bool exists = Directory.Exists(path) || File.Exists(path);
            bool? writable = Directory.Exists(path) ? await IsWritableAsync(path, cancellationToken) : null;
            paths.Add(new { path, exists, writable });
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? string.Empty;
                if (root.Length > 0 && disks.All(x => !string.Equals(((dynamic)x).path, root, StringComparison.OrdinalIgnoreCase)))
                {
                    DriveInfo drive = new(root);
                    if (drive.IsReady) disks.Add(new { path = root, freeBytes = drive.AvailableFreeSpace, totalBytes = drive.TotalSize });
                }
            }
            catch { }
        }

        List<object> endpoints = [];
        foreach ((string name, string? endpoint) in KnownEndpoints())
        {
            if (string.IsNullOrWhiteSpace(endpoint)) continue;
            endpoints.Add(new { name, endpoint, reachable = await ReachableAsync(endpoint, cancellationToken) });
        }

        Process process = Process.GetCurrentProcess();
        GCMemoryInfo memory = GC.GetGCMemoryInfo();
        return new
        {
            operatingSystem = RuntimeInformation.OSDescription,
            operatingSystemVersion = Environment.OSVersion.VersionString,
            cpuArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            processorCount = Environment.ProcessorCount,
            availableMemoryBytes = memory.TotalAvailableMemoryBytes > 0 ? memory.TotalAvailableMemoryBytes : null,
            processWorkingSetBytes = process.WorkingSet64,
            runtimeVersion = RuntimeInformation.FrameworkDescription,
            networkAvailable = NetworkInterface.GetIsNetworkAvailable(),
            disks,
            paths,
            endpoints,
            hardware = Array.Empty<object>(),
            detectedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private IEnumerable<string> RelevantPaths()
    {
        yield return environment.ContentRootPath;
        foreach (string key in new[] { "Storage:AttachmentsPath", "Storage:MediaArchivePath" })
            if (configuration[key] is { Length: > 0 } value) yield return value;
    }

    private IEnumerable<(string Name, string? Endpoint)> KnownEndpoints()
    {
        yield return ("Aegis.Configuration", First(configuration["Aegis:Configuration:Url"], configuration["AegisConfiguration:BaseUrl"]));
        yield return ("Aegis.Diagnostics", configuration["Aegis:Diagnostics:Url"]);
        if (configuration.GetValue("MicrosoftGraphEmail:Enabled", true)) yield return ("Microsoft Graph", "https://graph.microsoft.com");
    }

    private static async Task<bool?> ReachableAsync(string? endpoint, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return null;
        try
        {
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(3) };
            using HttpRequestMessage request = new(HttpMethod.Head, uri);
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    private static async Task<bool> IsWritableAsync(string path, CancellationToken cancellationToken)
    {
        string probe = Path.Combine(path, $".aegis-write-probe-{Guid.NewGuid():N}");
        try { await File.WriteAllTextAsync(probe, string.Empty, cancellationToken); File.Delete(probe); return true; }
        catch { try { if (File.Exists(probe)) File.Delete(probe); } catch { } return false; }
    }

    private static bool SecretZeroDetected() => HasAnyValue(
        Environment.GetEnvironmentVariable("OPENBAO_SECRET_ID"),
        Environment.GetEnvironmentVariable("OPENBAO_TOKEN"),
        Environment.GetEnvironmentVariable("VAULT_TOKEN"));
    private static bool HasValue(string? value) => !string.IsNullOrWhiteSpace(value);
    private static bool HasAnyValue(params string?[] values) => values.Any(HasValue);
    private static string? First(params string?[] values) => values.FirstOrDefault(HasValue);
}
