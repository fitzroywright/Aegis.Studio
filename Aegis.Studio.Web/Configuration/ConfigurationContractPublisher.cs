using System.Reflection;
using System.Text.Json.Nodes;
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
        string? baseUrl = configuration["AegisConfiguration:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            logger.LogInformation("Aegis.Configuration publication is disabled because AegisConfiguration:BaseUrl is not configured.");
            return;
        }

        string path = configuration["AegisConfiguration:ContractPath"]
            ?? Path.Combine(environment.ContentRootPath, "configuration", "Aegis.Studio.configuration-contract.json");

        while (!stoppingToken.IsCancellationRequested)
        {
            if (await TryPublishAsync(baseUrl, path, stoppingToken))
            {
                return;
            }

            try
            {
                await Task.Delay(RetryDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task<bool> TryPublishAsync(string baseUrl, string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path))
            {
                logger.LogWarning("Aegis.Configuration contract was not found at {ContractPath}; Studio remains operational and will retry publication.", path);
                return false;
            }

            string? key = await secrets.GetAsync($"configuration/registration/{ApplicationId}", cancellationToken);
            if (string.IsNullOrWhiteSpace(key))
            {
                logger.LogWarning("Aegis.Configuration registration credential is unavailable; Studio remains operational and will retry publication.");
                return false;
            }

            JsonNode contractNode = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken))
                ?? throw new InvalidDataException("Configuration contract is empty.");
            if (contractNode is not JsonObject contract)
            {
                throw new InvalidDataException("Configuration contract must be a JSON object.");
            }

            contract["version"] = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
            contract["siteId"] = NullIfBlank(configuration["Site:Id"]);
            contract["instanceId"] = NullIfBlank(configuration["Service:Identity"]);

            bool databaseConfigured = HasValue(await secrets.GetAsync("ConnectionStrings:Studio", cancellationToken))
                || HasNonPlaceholderValue(configuration.GetConnectionString("Studio"));
            bool graphSecretConfigured = HasValue(await secrets.GetAsync("Studio:MicrosoftGraphEmail:ClientSecret", cancellationToken));
            bool openBaoCredentialsConfigured = HasAnyValue(
                configuration["Secrets:OpenBao:Token"],
                configuration["OpenBao:Token"],
                configuration["Secrets:OpenBao:RoleId"],
                Environment.GetEnvironmentVariable("OPENBAO_TOKEN"),
                Environment.GetEnvironmentVariable("VAULT_TOKEN"));

            JsonArray requirements = contract["requirements"] as JsonArray
                ?? throw new InvalidDataException("Configuration contract has no requirements array.");

            foreach (JsonNode? node in requirements)
            {
                if (node is not JsonObject requirement)
                {
                    continue;
                }

                string id = requirement["id"]?.GetValue<string>() ?? string.Empty;
                requirement["isConfigured"] = id switch
                {
                    "database" => databaseConfigured,
                    "graph-tenant" => HasValue(configuration["MicrosoftGraphEmail:TenantId"]),
                    "graph-client" => HasValue(configuration["MicrosoftGraphEmail:ClientId"]),
                    "graph-client-secret" => graphSecretConfigured,
                    "graph-mailbox" => HasValue(configuration["MicrosoftGraphEmail:Mailbox"]),
                    "attachments-storage" => HasValue(configuration["Storage:AttachmentsPath"]),
                    "gallery-storage" => HasValue(configuration["Storage:GalleriesPath"]),
                    "media-archive" => HasValue(configuration["Storage:MediaArchivePath"]),
                    "openbao-url" => HasAnyValue(
                        configuration["Secrets:OpenBao:Address"],
                        configuration["OpenBao:Address"],
                        Environment.GetEnvironmentVariable("OPENBAO_ADDR"),
                        Environment.GetEnvironmentVariable("VAULT_ADDR")),
                    "openbao-credentials" => openBaoCredentialsConfigured,
                    "site-id" => HasValue(configuration["Site:Id"]),
                    "service-identity" => HasValue(configuration["Service:Identity"]),
                    _ => false
                };
            }

            using HttpClient client = new();
            var registrar = new ConfigurationRegistrar(
                client,
                new RegistrationOptions(
                    new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute),
                    key,
                    TimeSpan.FromSeconds(10)));
            RegistrationResult result = await registrar.RegisterContractAsync(contract, cancellationToken);
            if (!result.Succeeded)
            {
                logger.LogWarning("Aegis.Configuration registration failed for Studio: {RegistrationError}; Studio remains operational and will retry.", result.Error ?? "Unknown registration failure.");
                return false;
            }

            logger.LogInformation("Aegis.Studio configuration requirements and configured state registered with Aegis.Configuration with HTTP {StatusCode}.", (int?)result.StatusCode);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to publish Aegis.Studio configuration requirements; Studio remains operational and will retry publication.");
            return false;
        }
    }

    private static bool HasValue(string? value) => !string.IsNullOrWhiteSpace(value);

    private static bool HasAnyValue(params string?[] values) => values.Any(HasValue);

    private static bool HasNonPlaceholderValue(string? value) =>
        value is not null
        && !string.IsNullOrWhiteSpace(value)
        && !value.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase);

    private static string? NullIfBlank(string? value) => HasValue(value) ? value : null;
}
