using Common.Diagnostics;
using Common.Secrets;

namespace Aegis.Studio.Web.Diagnostics;

public sealed class BasicRuntimeIdentityDiagnosticLevelTest(
    IConfiguration configuration,
    IHostEnvironment environment) : IDiagnosticLevelLocalTest
{
    public string TestId => "AEGIS.STUDIO.WEB.L5.RUNTIME.IDENTITY";
    public string Name => "Runtime identity";
    public string Owner => "Aegis.Studio.Web";
    public Common.Diagnostics.EngineeringDiagnosticLevel Level => Common.Diagnostics.EngineeringDiagnosticLevel.Level5Scan;
    public bool IsDestructive => false;

    public Task<Common.Diagnostics.EngineeringDiagnosticCheckResult> RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string instanceId =
            configuration["Service:Identity"]?.Trim()
            ?? configuration["Client:InstallationId"]?.Trim()
            ?? Environment.MachineName;

        return Task.FromResult(string.IsNullOrWhiteSpace(instanceId)
            ? EngineeringDiagnosticPolicy.Warning(
                TestId,
                Name,
                "No stable runtime identity could be resolved.")
            : EngineeringDiagnosticPolicy.Passed(
                TestId,
                Name,
                "Runtime identity is available.",
                $"Instance={instanceId}; Environment={environment.EnvironmentName}; Machine={Environment.MachineName}"));
    }
}

public sealed class BasicControlPlaneConfigurationDiagnosticLevelTest(
    IConfiguration configuration) : IDiagnosticLevelLocalTest
{
    public string TestId => "AEGIS.STUDIO.WEB.L5.CONTROLPLANE.CONFIG";
    public string Name => "Control Plane configuration";
    public string Owner => "Aegis.Studio.Web";
    public Common.Diagnostics.EngineeringDiagnosticLevel Level => Common.Diagnostics.EngineeringDiagnosticLevel.Level5Scan;
    public bool IsDestructive => false;

    public Task<Common.Diagnostics.EngineeringDiagnosticCheckResult> RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string[] required =
        [
            "Aegis:Configuration:Url",
            "Aegis:Diagnostics:Url",
            "Aegis:Operations:Url",
            "Aegis:Diagnostics:RequestCredentialSecretName"
        ];

        string[] missing = required
            .Where(key => string.IsNullOrWhiteSpace(configuration[key]))
            .ToArray();

        return Task.FromResult(missing.Length == 0
            ? EngineeringDiagnosticPolicy.Passed(
                TestId,
                Name,
                "Required Control Plane settings are configured.")
            : EngineeringDiagnosticPolicy.Warning(
                TestId,
                Name,
                "One or more Control Plane settings are missing.",
                $"Missing={string.Join(",", missing)}"));
    }
}


public sealed class CommonSecretsAvailabilityDiagnosticLevelTest(
    IConfiguration configuration,
    ISecretProvider secrets) : IDiagnosticLevelLocalTest
{
    public string TestId => "AEGIS.STUDIO.WEB.L5.SECRETS.AVAILABILITY";
    public string Name => "Required secret availability";
    public string Owner => "Aegis.Studio.Web";
    public Common.Diagnostics.EngineeringDiagnosticLevel Level => Common.Diagnostics.EngineeringDiagnosticLevel.Level5Scan;
    public bool IsDestructive => false;

    public async Task<Common.Diagnostics.EngineeringDiagnosticCheckResult> RunAsync(CancellationToken cancellationToken)
    {
        var required = new List<(string Label, string SecretName)>
        {
            ("Studio database", "ConnectionStrings:Studio")
        };

        string? diagnosticCredentialName =
            configuration["Aegis:Diagnostics:RequestCredentialSecretName"]?.Trim();
        if (!string.IsNullOrWhiteSpace(diagnosticCredentialName))
            required.Add(("Diagnostics request credential", diagnosticCredentialName));

        if (configuration.GetValue("MicrosoftGraphEmail:Enabled", false))
            required.Add(("Studio Microsoft Graph email client secret", "Studio:MicrosoftGraphEmail:ClientSecret"));

        if (configuration.GetValue("Messaging:MicrosoftGraph:Email:Enabled", false))
            required.Add((
                "Common.Messaging Microsoft Graph client secret",
                configuration["Messaging:MicrosoftGraph:ClientSecretName"]?.Trim()
                    ?? "messaging/msgraph/client-secret"));

        if (configuration.GetValue("Messaging:MicrosoftGraph:Teams:Enabled", false))
            required.Add((
                "Common.Messaging Teams delegated token",
                configuration["Messaging:MicrosoftGraph:Teams:DelegatedAccessTokenSecretName"]?.Trim()
                    ?? "messaging/msgraph/teams/delegated-access-token"));

        string storageProvider = configuration["Storage:Provider"]?.Trim() ?? "NetworkFolder";
        string? storageSecretName = storageProvider.ToLowerInvariant() switch
        {
            "sharepoint" => configuration["Storage:SharePoint:ClientSecretName"]?.Trim()
                ?? "storage/sharepoint/client-secret",
            "azureblob" => configuration["Storage:AzureBlob:ConnectionStringSecretName"]?.Trim()
                ?? "storage/azureblob/connection-string",
            "amazons3" => configuration["Storage:AmazonS3:SecretAccessKeyName"]?.Trim()
                ?? "storage/amazons3/secret-access-key",
            _ => null
        };
        if (!string.IsNullOrWhiteSpace(storageSecretName))
            required.Add(($"Storage provider {storageProvider}", storageSecretName));

        var missing = new List<string>();
        foreach ((string label, string secretName) in required)
        {
            try
            {
                string? value = await secrets.GetAsync(secretName, cancellationToken);
                if (string.IsNullOrWhiteSpace(value))
                    missing.Add(label);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                missing.Add($"{label}:resolve-{exception.GetType().Name}");
            }
        }

        return missing.Count == 0
            ? EngineeringDiagnosticPolicy.Passed(
                TestId,
                Name,
                "All currently required Studio secrets resolve through Common.Secrets.",
                $"Checked={required.Count}; SecretValuesExposed=false")
            : EngineeringDiagnosticPolicy.Failed(
                TestId,
                Name,
                "One or more currently required Studio secrets cannot be resolved through Common.Secrets.",
                $"MissingOrUnreadable={string.Join(",", missing)}; Checked={required.Count}");
    }
}
