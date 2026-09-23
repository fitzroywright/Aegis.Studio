using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using Common.Diagnostics;
using Common.Registration;
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
        string configurationUrl = AegisControlPlaneEndpoints.ResolvePublic(
            configuration,
            AegisControlPlaneService.Configuration,
            logger,
            configurationKey: "Aegis:Configuration:Url");
        string operationsUrl = AegisControlPlaneEndpoints.ResolvePublic(
            configuration,
            AegisControlPlaneService.Operations,
            logger,
            configurationKey: "Aegis:Operations:Url");

        string path = configuration["AegisConfiguration:ContractPath"]
            ?? Path.Combine(environment.ContentRootPath, "configuration", "Aegis.Studio.configuration-contract.json");

        while (!stoppingToken.IsCancellationRequested)
        {
            await TryPublishAsync(configurationUrl, operationsUrl, path, stoppingToken);
            try { await Task.Delay(RetryDelay, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    private async Task<bool> TryPublishAsync(
        string configurationUrl,
        string operationsUrl,
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path))
            {
                logger.LogWarning("Aegis.Configuration contract was not found at {ContractPath}; Studio remains operational and will retry publication.", path);
                return false;
            }

            JsonNode contractNode = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken))
                ?? throw new InvalidDataException("Configuration contract is empty.");
            if (contractNode is not JsonObject contract) throw new InvalidDataException("Configuration contract must be a JSON object.");

            contract["version"] = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
            contract["module"] = "Aegis.Studio.Web";
            contract["runbookReference"] = "docs/RUNBOOK.md";
            contract["siteId"] = NullIfBlank(configuration["Site:Id"]);
            string instanceId =
                NullIfBlank(configuration["Service:Identity"])
                ?? Environment.MachineName;
            contract["instanceId"] = instanceId;
            contract["runtimeEnvironment"] = new JsonObject
            {
                ["machineName"] = Environment.MachineName,
                ["operatingSystem"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                ["processArchitecture"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                ["frameworkDescription"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                ["hostingEnvironment"] = environment.EnvironmentName
            };
            contract["presentation"] = new JsonObject
            {
                ["iconUrl"] = configuration["Aegis:Presentation:IconUrl"],
                ["shortName"] = "Studio",
                ["accent"] = configuration["Aegis:Presentation:Accent"] ?? "gold"
            };
            string? diagnosticSecretName =
                NullIfBlank(configuration["Aegis:Diagnostics:RequestCredentialSecretName"]);
            contract["diagnostics"] = new JsonObject
            {
                ["baseUrl"] = NullIfBlank(configuration["Aegis:PublicUrl"]),
                ["healthPath"] = "/health",
                ["diagnosticsRunPath"] = "/api/engineering/diagnostics/diagnostic-level/run",
                ["diagnosticsRunsPath"] = "/api/engineering/diagnostics/diagnostic-level/runs",
                ["supportsRemoteDiagnostics"] = true,
                ["supportsOperationalTelemetry"] = true,
                ["authenticationScheme"] = "DiagnosticLevel-HMAC-SHA256",
                ["secretName"] = diagnosticSecretName,
                ["supportedLevels"] = new JsonArray(5, 4, 3, 2, 1)
            };

            bool databaseConfigured = HasValue(await secrets.GetAsync("ConnectionStrings:Studio", cancellationToken));
            bool graphSecretConfigured = HasValue(await secrets.GetAsync("Studio:MicrosoftGraphEmail:ClientSecret", cancellationToken));
            bool graphEmailEnabled = configuration.GetValue("Messaging:MicrosoftGraph:Email:Enabled", false);
            bool graphTeamsEnabled = configuration.GetValue("Messaging:MicrosoftGraph:Teams:Enabled", false);
            bool messagingGraphSecretConfigured = !graphEmailEnabled || HasValue(await secrets.GetAsync(configuration["Messaging:MicrosoftGraph:ClientSecretName"] ?? "messaging/msgraph/client-secret", cancellationToken));
            bool messagingTeamsTokenConfigured = !graphTeamsEnabled || HasValue(await secrets.GetAsync(configuration["Messaging:MicrosoftGraph:Teams:DelegatedAccessTokenSecretName"] ?? "messaging/msgraph/teams/delegated-access-token", cancellationToken));

            JsonArray requirements = contract["requirements"] as JsonArray
                ?? throw new InvalidDataException("Configuration contract has no requirements array.");

            foreach (JsonNode? node in requirements)
            {
                if (node is not JsonObject requirement) continue;

                string id = requirement["id"]?.GetValue<string>() ?? string.Empty;
                bool required = requirement["required"]?.GetValue<bool>() ?? false;
                (bool configured, string state, string? verification) = id switch
                {
                    "database" => State(databaseConfigured, required, "ST-CONFIG-DB-001: Studio PostgreSQL connection string must resolve through Common.Secrets."),
                    "graph-tenant" => State(HasValue(configuration["MicrosoftGraphEmail:TenantId"]), required, "ST-CONFIG-MAIL-001: Studio mailbox routing requires MicrosoftGraphEmail:TenantId."),
                    "graph-client" => State(HasValue(configuration["MicrosoftGraphEmail:ClientId"]), required, "ST-CONFIG-MAIL-002: Studio mailbox routing requires MicrosoftGraphEmail:ClientId."),
                    "graph-client-secret" => State(graphSecretConfigured, required, "ST-CONFIG-MAIL-003: Studio mailbox routing client secret must resolve through Common.Secrets."),
                    "graph-mailbox" => State(HasValue(configuration["MicrosoftGraphEmail:Mailbox"]), required, "ST-CONFIG-MAIL-004: Studio mailbox routing requires MicrosoftGraphEmail:Mailbox."),
                    "messaging-graph-tenant" => OptionalDependencyState(graphEmailEnabled, HasValue(configuration["Messaging:MicrosoftGraph:TenantId"]), "ST-MSG-GRAPH-EMAIL-001: Graph Email requires Messaging:MicrosoftGraph:TenantId."),
                    "messaging-graph-client" => OptionalDependencyState(graphEmailEnabled, HasValue(configuration["Messaging:MicrosoftGraph:ClientId"]), "ST-MSG-GRAPH-EMAIL-002: Graph Email requires Messaging:MicrosoftGraph:ClientId."),
                    "messaging-graph-client-secret" => OptionalDependencyState(graphEmailEnabled, messagingGraphSecretConfigured, "ST-MSG-GRAPH-EMAIL-003: Graph Email requires its client secret through Common.Secrets."),
                    "messaging-graph-email-enabled" => OptionalFeatureState(graphEmailEnabled),
                    "messaging-graph-email-sender" => OptionalDependencyState(graphEmailEnabled, HasValue(configuration["Messaging:MicrosoftGraph:Email:SenderUpn"]), "ST-MSG-GRAPH-EMAIL-004: Graph Email requires a sender UPN when enabled."),
                    "messaging-graph-teams-enabled" => OptionalFeatureState(graphTeamsEnabled),
                    "messaging-graph-teams-sender" => OptionalDependencyState(graphTeamsEnabled, HasValue(configuration["Messaging:MicrosoftGraph:Teams:SenderUpn"]), "ST-MSG-GRAPH-TEAMS-001: Graph Teams requires a sender UPN when enabled."),
                    "messaging-graph-teams-token" => OptionalDependencyState(graphTeamsEnabled, messagingTeamsTokenConfigured, "ST-MSG-GRAPH-TEAMS-002: Graph Teams requires its delegated token through Common.Secrets."),
                    "attachments-storage" => State(HasValue(configuration["Storage:AttachmentsPath"]), required, "ST-CONFIG-STORAGE-001: Storage:AttachmentsPath must be configured."),
                    "gallery-storage" => State(HasValue(configuration["Storage:GalleriesPath"]), required, "ST-CONFIG-STORAGE-002: Storage:GalleriesPath must be configured."),
                    "media-archive" => State(HasValue(configuration["Storage:MediaArchivePath"]), required, "ST-CONFIG-STORAGE-003: Storage:MediaArchivePath must be configured."),
                    "site-id" => State(HasValue(configuration["Site:Id"]), required, "ST-CONFIG-SITE-001: Site:Id must identify this deployment."),
                    "service-identity" => State(HasValue(configuration["Service:Identity"]), required, "ST-CONFIG-IDENTITY-001: Service:Identity must identify this Studio instance."),
                    "public-url" => State(HasValue(configuration["Aegis:PublicUrl"]), required, "ST-CONFIG-PUBLIC-URL-001: Aegis:PublicUrl must identify the externally reachable Studio endpoint."),
                    _ => (false, "Unknown", "No runtime commissioning evaluator is defined for this requirement.")
                };

                requirement["isConfigured"] = configured;
                requirement["configurationState"] = state;
                requirement["effectiveValueAvailable"] = configured;
                requirement["verificationStatus"] = state == "Disabled" ? "NOT REQUIRED" : configured ? "PASS" : verification;
                if (id == "public-url")
                    requirement["displayValue"] = configured ? configuration["Aegis:PublicUrl"]?.Trim() : null;
            }

            string identityFile =
                configuration["Aegis:Registration:IdentityFile"]
                ?? (OperatingSystem.IsWindows()
                    ? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "Aegis",
                        "Studio",
                        "registration-identity.json")
                    : "/var/lib/aegis/studio/registration-identity.json");

            using HttpClient client = new();
            using HttpClient lifecycleClient = new();
            ILifecycleEventSink lifecycle = CreateLifecycleSink(
                operationsUrl,
                lifecycleClient,
                identityFile,
                instanceId);

            var registration = new RegistrationLifecycleClient(
                client,
                new RegistrationLifecycleOptions(
                    new Uri(configurationUrl.TrimEnd('/') + "/", UriKind.Absolute),
                    ApplicationId,
                    instanceId,
                    identityFile,
                    TimeSpan.FromSeconds(Math.Clamp(
                        configuration.GetValue("Aegis:Registration:RequestTimeoutSeconds", 30),
                        5,
                        120)),
                    RunbookReference: "docs/RUNBOOK.md"),
                logger: logger,
                lifecycleEventSink: lifecycle);

            RegistrationLifecycleStatus result =
                await registration.StepAsync(
                    new JsonObject
                    {
                        ["displayName"] = "Aegis Studio",
                        ["module"] = "Aegis.Studio.Web",
                        ["presentation"] = new JsonObject
                        {
                            ["iconUrl"] = configuration["Aegis:Presentation:IconUrl"],
                            ["shortName"] = "Studio",
                            ["accent"] = configuration["Aegis:Presentation:Accent"] ?? "gold"
                        },
                        ["siteId"] = contract["siteId"]?.DeepClone(),
                        ["hostname"] = Environment.MachineName,
                        ["runtimeEnvironment"] = environment.EnvironmentName
                    },
                    cancellationToken);

            if (!result.IsRegistered)
            {
                logger.LogInformation(
                    "Aegis.Studio registration state is {RegistrationState}: {RegistrationError}; Studio remains operational.",
                    result.State,
                    result.Error ?? "No additional detail.");
                return false;
            }

            HttpStatusCode contractStatus =
                await registration.PublishConfigurationContractAsync(contract, cancellationToken);

            if ((int)contractStatus is >= 200 and < 300)
            {
                logger.LogInformation(
                    "Aegis.Studio registration is valid and its configuration contract was published.");
                return true;
            }

            logger.LogWarning(
                "Aegis.Studio is registered, but Configuration contract publication returned HTTP {StatusCode}.",
                (int)contractStatus);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to publish Aegis.Studio configuration requirements; Studio remains operational and will retry publication.");
            return false;
        }
    }

    private static ILifecycleEventSink CreateLifecycleSink(
        string operationsUrl,
        HttpClient client,
        string identityFile,
        string instanceId)
    {
        if (!Uri.TryCreate(
                operationsUrl.TrimEnd('/') + "/api/operations/activity/observe",
                UriKind.Absolute,
                out Uri? endpoint))
            return new NullLifecycleEventSink();

        var store = new FileRegistrationIdentityStore(identityFile);
        return new HttpLifecycleEventSink(
            client,
            new HttpLifecycleEventSinkOptions(endpoint, TimeSpan.FromSeconds(10)),
            async (request, lifecycleEvent, cancellationToken) =>
            {
                RegistrationIdentityDocument identity =
                    await store.LoadOrCreateAsync(
                        ApplicationId,
                        instanceId,
                        cancellationToken);

                if (string.IsNullOrWhiteSpace(identity.Credential))
                    return;

                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue(
                        "Bearer",
                        identity.Credential);
                request.Headers.TryAddWithoutValidation(
                    "X-Aegis-Application-Id",
                    identity.ApplicationId);
                request.Headers.TryAddWithoutValidation(
                    "X-Aegis-Instance-Id",
                    identity.InstanceId);
                request.Headers.TryAddWithoutValidation(
                    "X-Aegis-Installation-Id",
                    identity.InstallationId);
            });
    }

    private static (bool Configured, string State, string? Verification) State(bool configured, bool required, string message)
        => configured ? (true, "Configured", null) : required ? (false, "Missing", message) : (false, "Disabled", null);

    private static (bool Configured, string State, string? Verification) OptionalFeatureState(bool enabled)
        => enabled ? (true, "Configured", null) : (false, "Disabled", null);

    private static (bool Configured, string State, string? Verification) OptionalDependencyState(bool enabled, bool configured, string message)
        => !enabled ? (false, "Disabled", null) : configured ? (true, "Configured", null) : (false, "Missing", message);

    private static bool HasValue(string? value) => !string.IsNullOrWhiteSpace(value);
    private static string? NullIfBlank(string? value) => HasValue(value) ? value : null;
}
