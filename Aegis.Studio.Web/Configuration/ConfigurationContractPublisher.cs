using System.Text;
using Common.Secrets;

namespace Aegis.Studio.Web.Configuration;

public sealed class ConfigurationContractPublisher(
    IConfiguration configuration,
    IWebHostEnvironment environment,
    ISecretProvider secrets,
    ILogger<ConfigurationContractPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? baseUrl = configuration["AegisConfiguration:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            logger.LogInformation("Aegis.Configuration contract publication is disabled because AegisConfiguration:BaseUrl is not configured.");
            return;
        }

        string path = configuration["AegisConfiguration:ContractPath"]
            ?? Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "configuration", "Aegis.Studio.configuration-contract.json"));

        try
        {
            if (!File.Exists(path))
            {
                logger.LogWarning("Aegis.Configuration contract was not found at {ContractPath}; Studio will continue without publishing it.", path);
                return;
            }

            string json = await File.ReadAllTextAsync(path, stoppingToken);
            string? key = await secrets.GetAsync("configuration/registration/Aegis.Studio", stoppingToken);
            if (string.IsNullOrWhiteSpace(key))
            {
                logger.LogWarning("Aegis.Configuration registration credential is unavailable; Studio will continue without publishing its contract.");
                return;
            }

            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(10) };
            using HttpRequestMessage request = new(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/contracts/register");
            request.Headers.TryAddWithoutValidation("X-Configuration-Registration-Key", key);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await client.SendAsync(request, stoppingToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Aegis.Configuration rejected Studio contract publication with HTTP {StatusCode}; Studio remains operational.", (int)response.StatusCode);
                return;
            }

            logger.LogInformation("Published Aegis.Studio configuration contract to Aegis.Configuration.");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to publish Aegis.Studio configuration contract; Studio remains operational.");
        }
    }
}
