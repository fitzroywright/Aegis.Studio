using System.Text.Json;
using Aegis.Studio.Web.Persistence;
using Common.Secrets;
using Common.Security.Integration;
using Npgsql;

namespace Aegis.Studio.Web.Operations;

public static class StudioMaintenanceCommand
{
    private const string Usage = "Studio offline maintenance: maintenance bootstrap-admin --login LOGIN --subject SUBJECT --account-id ID --actor AUDIT_ACTOR --confirm-isolation [--apply --confirm-offline]. Default: read-only plan. Stop Studio before applying. Production is prohibited.";

    public static async Task<StudioMaintenanceDatabaseIdentity> ReadDatabaseIdentityAsync(NpgsqlDataSource source)
    {
        await using var metadata = await source.OpenConnectionAsync();
        await using var query = new NpgsqlCommand("select current_database(),current_schema(),coalesce(host(inet_server_addr()),''),inet_server_port()", metadata);
        await using var reader = await query.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? 0 : reader.GetInt32(3));
    }

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args is ["version"])
            {
                string version = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(StudioMaintenanceCommand).Assembly)?.InformationalVersion ?? "unknown";
                Console.WriteLine(JsonSerializer.Serialize(new { mode = "version", informationalVersion = version }));
                return 0;
            }
            if (args is ["--help"] or ["bootstrap-admin", "--help"])
            {
                Console.WriteLine(Usage);
                return 0;
            }
            if (args.Length == 0 || args[0] != "bootstrap-admin")
                throw new ArgumentException(Usage);
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var flags = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 1; i < args.Length; i++)
            {
                string option = args[i];
                if (option is "--apply" or "--confirm-isolation" or "--confirm-offline")
                { if (!flags.Add(option)) throw new ArgumentException("Duplicate flag."); }
                else if (option is "--login" or "--subject" or "--account-id" or "--actor" or "--expected-database")
                {
                    if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(option, args[i]))
                        throw new ArgumentException("Missing or duplicated argument.");
                }
                else throw new ArgumentException("Unknown maintenance option.");
            }
            string Required(string key) => values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
                ? value : throw new ArgumentException("Missing " + key);
            var request = new AdministratorBootstrapRequest(Required("--login"), Required("--subject"), Required("--account-id"), Required("--actor"));
            // Use the same content-root/appsettings/environment and secret factory as the web host,
            // without constructing or starting its host, background workers or network listeners.
            var builder = WebApplication.CreateBuilder(Array.Empty<string>());
            if (!flags.Contains("--confirm-isolation")) throw new InvalidOperationException("Review database, storage and integrations before confirming isolation.");
            var mock = MockAuthenticationConfiguration.Read(builder.Configuration.GetSection("MockAuthentication"), builder.Environment.EnvironmentName, "Aegis.Studio");
            if (!Common.Security.Authenticators.MockEnvironmentPolicy.IsPermitted(mock.Enabled, mock.EnvironmentName, mock.IsolationConfirmed))
                throw new InvalidOperationException("Bootstrap requires isolated Demo or Development configuration; Production is prohibited.");
            if (flags.Contains("--apply") && !flags.Contains("--confirm-offline"))
                throw new InvalidOperationException("Apply requires a stopped Studio service and --confirm-offline.");
            ISecretProvider secrets = CommonSecretProviderFactory.Create(builder.Configuration);
            string connection = await secrets.GetRequiredAsync("ConnectionStrings:Studio");
            await using var source = NpgsqlDataSource.Create(connection);
            StudioMaintenanceDatabaseIdentity databaseIdentity = await ReadDatabaseIdentityAsync(source);
            if (values.TryGetValue("--expected-database", out string? expected))
            {
                var reviewed = JsonSerializer.Deserialize<StudioMaintenanceDatabaseIdentity>(expected, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (reviewed != databaseIdentity) throw new InvalidOperationException("The resolved database does not match the reviewed cutover target.");
            }
            var operation = new OfflineAdministratorBootstrap(new StudioDatabase(source, connection), mock);
            var result = flags.Contains("--apply") ? operation.Apply(request, true) : operation.Plan(request);
            Console.WriteLine(JsonSerializer.Serialize(new { mode = flags.Contains("--apply") ? "applied" : "plan", result,
                database = new { name = databaseIdentity.Name, schema = databaseIdentity.Schema, serverAddress = databaseIdentity.ServerAddress, port = databaseIdentity.Port } }));
            return 0;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or System.Data.Common.DbException or JsonException)
        {
            // Provider/database exception text can contain credentials or protected configuration.
            Console.Error.WriteLine("Studio maintenance failed (" + error.GetType().Name + "). No credential or provider details are printed. Review the protected configuration and selected identity.");
            return 1;
        }
    }
}

public sealed record StudioMaintenanceDatabaseIdentity(string Name, string Schema, string ServerAddress, int Port);
