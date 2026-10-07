using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Aegis.Studio.Application.EmailRouting;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Profiles;
using Npgsql;

namespace Aegis.Studio.Web.Messaging;

public sealed class StudioMessageAddressBook
{
    private static readonly Regex ReferencePattern = new(@"\[AEGIS-([A-Fa-f0-9]{8})\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly StudioDatabase database;
    private readonly UserProfileStore profiles;
    private readonly IConfiguration configuration;

    public StudioMessageAddressBook(StudioDatabase database, UserProfileStore profiles, IConfiguration configuration)
    {
        this.database = database;
        this.profiles = profiles;
        this.configuration = configuration;
    }

    public StudioMessageAddress EnsurePerson(StudioUserProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string address = PersonAddress(profile.UserName);
        Guid workspaceId = StableId("person-mailbox:" + profile.UserName.Trim().ToLowerInvariant());
        DateTimeOffset now = DateTimeOffset.UtcNow;

        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlTransaction tx = connection.BeginTransaction();

        using (NpgsqlCommand workspace = connection.CreateCommand())
        {
            workspace.Transaction = tx;
            workspace.CommandText = """
                insert into studio_workspaces
                    (id,name,email_address,route_key,is_enabled,sort_order,created_at_utc,updated_at_utc)
                values ($1,$2,$3,$4,false,2147483000,$5,$5)
                on conflict(id) do update set
                    name=excluded.name,email_address=excluded.email_address,route_key=excluded.route_key,
                    is_enabled=false,updated_at_utc=excluded.updated_at_utc
                """;
            workspace.Parameters.AddWithValue(workspaceId);
            workspace.Parameters.AddWithValue("Personal mailbox — " + profile.DisplayName);
            workspace.Parameters.AddWithValue(address);
            workspace.Parameters.AddWithValue("person:" + profile.UserName.Trim().ToLowerInvariant());
            workspace.Parameters.AddWithValue(now);
            workspace.ExecuteNonQuery();
        }

        using (NpgsqlCommand identity = connection.CreateCommand())
        {
            identity.Transaction = tx;
            identity.CommandText = """
                insert into studio_message_addresses
                    (address,destination_type,person_user_name,workspace_id,job_id,display_name,is_active,created_at_utc,updated_at_utc)
                values ($1,'Person',$2,$3,null,$4,true,$5,$5)
                on conflict(address) do update set
                    destination_type='Person',person_user_name=excluded.person_user_name,
                    workspace_id=excluded.workspace_id,job_id=null,display_name=excluded.display_name,
                    is_active=true,updated_at_utc=excluded.updated_at_utc
                """;
            identity.Parameters.AddWithValue(address);
            identity.Parameters.AddWithValue(profile.UserName);
            identity.Parameters.AddWithValue(workspaceId);
            identity.Parameters.AddWithValue(profile.DisplayName);
            identity.Parameters.AddWithValue(now);
            identity.ExecuteNonQuery();
        }

        tx.Commit();
        return new StudioMessageAddress(address, "Person", profile.DisplayName, workspaceId, null, profile.UserName);
    }

    public StudioMessageAddress EnsureCurrentPerson(System.Security.Claims.ClaimsPrincipal user)
        => EnsurePerson(profiles.GetOrCreate(user));

    public StudioMessageAddress? ResolveAddress(string? value)
    {
        string address = NormalizeAddress(value);
        if (string.IsNullOrWhiteSpace(address)) return null;

        StudioUserProfile? inferredPerson = profiles.GetAll().FirstOrDefault(profile =>
            string.Equals(PersonAddress(profile.UserName), address, StringComparison.OrdinalIgnoreCase));
        if (inferredPerson is not null) return EnsurePerson(inferredPerson);

        using NpgsqlConnection connection = database.OpenConnection();
        using (NpgsqlCommand configured = connection.CreateCommand())
        {
            configured.CommandText = """
                select address,destination_type,display_name,workspace_id,job_id,person_user_name
                from studio_message_addresses
                where lower(address)=lower($1) and is_active
                """;
            configured.Parameters.AddWithValue(address);
            using NpgsqlDataReader reader = configured.ExecuteReader();
            if (reader.Read()) return Read(reader);
        }

        using (NpgsqlCommand workspace = connection.CreateCommand())
        {
            workspace.CommandText = """
                select email_address,'Workspace',name,id,null::uuid,null::text
                from studio_workspaces
                where is_enabled and lower(email_address)=lower($1)
                """;
            workspace.Parameters.AddWithValue(address);
            using NpgsqlDataReader reader = workspace.ExecuteReader();
            if (reader.Read()) return Read(reader);
        }

        Match jobAddress = Regex.Match(address, @"^job\+([0-9a-fA-F-]{32,36})@", RegexOptions.CultureInvariant);
        if (jobAddress.Success && Guid.TryParse(jobAddress.Groups[1].Value, out Guid jobId))
        {
            using NpgsqlCommand job = connection.CreateCommand();
            job.CommandText = "select j.workspace_id,j.title from studio_jobs j where j.id=$1";
            job.Parameters.AddWithValue(jobId);
            using NpgsqlDataReader reader = job.ExecuteReader();
            if (reader.Read())
                return new StudioMessageAddress(address, "Job", reader.GetString(1), reader.GetGuid(0), jobId, null);
        }

        return null;
    }

    public StudioMessageAddress? ResolveRouteKey(string? routeKey)
    {
        string value = routeKey?.Trim() ?? string.Empty;
        if (value.StartsWith("person:", StringComparison.OrdinalIgnoreCase))
        {
            string userName = value[7..].Trim();
            StudioUserProfile? profile = profiles.GetAll().FirstOrDefault(p =>
                string.Equals(p.UserName, userName, StringComparison.OrdinalIgnoreCase));
            return profile is null ? null : EnsurePerson(profile);
        }

        if (value.StartsWith("job:", StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParse(value[4..], out Guid jobId))
        {
            using NpgsqlConnection connection = database.OpenConnection();
            using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText = "select workspace_id,title from studio_jobs where id=$1";
            command.Parameters.AddWithValue(jobId);
            using NpgsqlDataReader reader = command.ExecuteReader();
            return reader.Read()
                ? new StudioMessageAddress(JobAddress(jobId), "Job", reader.GetString(1), reader.GetGuid(0), jobId, null)
                : null;
        }

        return null;
    }

    public EmailRouteResult? ResolveReply(InboundEmail email)
    {
        using NpgsqlConnection connection = database.OpenConnection();

        if (!string.IsNullOrWhiteSpace(email.ThreadId))
        {
            using NpgsqlCommand thread = connection.CreateCommand();
            thread.CommandText = """
                select id
                from studio_conversations
                where external_thread_id=$1 and lower(contact_address)=lower($2) and not is_closed
                order by created_at_utc desc
                limit 2
                """;
            thread.Parameters.AddWithValue(email.ThreadId.Trim());
            thread.Parameters.AddWithValue(email.FromAddress.Trim());
            using NpgsqlDataReader reader = thread.ExecuteReader();
            List<Guid> matches = [];
            while (reader.Read()) matches.Add(reader.GetGuid(0));
            if (matches.Count == 1) return ClassifyConversation(matches[0]);
        }

        Match token = ReferencePattern.Match(email.Subject ?? string.Empty);
        if (!token.Success) return null;
        string prefix = token.Groups[1].Value.ToLowerInvariant();

        using NpgsqlCommand reference = connection.CreateCommand();
        reference.CommandText = """
            select id
            from studio_conversations
            where left(replace(id::text,'-',''),8)=lower($1)
              and lower(contact_address)=lower($2)
              and not is_closed
            limit 2
            """;
        reference.Parameters.AddWithValue(prefix);
        reference.Parameters.AddWithValue(email.FromAddress.Trim());
        using NpgsqlDataReader rows = reference.ExecuteReader();
        List<Guid> ids = [];
        while (rows.Read()) ids.Add(rows.GetGuid(0));
        return ids.Count == 1 ? ClassifyConversation(ids[0]) : null;
    }

    public EmailRouteResult ClassifyConversation(Guid conversationId)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            select c.workspace_id,c.job_id,w.name,
                   a.destination_type,a.display_name,a.person_user_name,
                   j.title
            from studio_conversations c
            join studio_workspaces w on w.id=c.workspace_id
            left join studio_message_addresses a on a.workspace_id=c.workspace_id and a.destination_type='Person' and a.is_active
            left join studio_jobs j on j.id=c.job_id
            where c.id=$1
            """;
        command.Parameters.AddWithValue(conversationId);
        using NpgsqlDataReader reader = command.ExecuteReader();
        if (!reader.Read())
            return new EmailRouteResult(false,null,null,null,"Conversation route was not found.","System",null,null,"Aegis Studio");

        Guid workspaceId = reader.GetGuid(0);
        Guid? jobId = reader.IsDBNull(1) ? null : reader.GetGuid(1);
        string workspaceName = reader.GetString(2);
        if (jobId.HasValue)
        {
            string jobName = reader.IsDBNull(6) ? "Job" : reader.GetString(6);
            return new EmailRouteResult(true,workspaceId,workspaceName,null,null,"Job",jobId,null,jobName);
        }

        if (!reader.IsDBNull(3) && string.Equals(reader.GetString(3),"Person",StringComparison.OrdinalIgnoreCase))
        {
            string display = reader.IsDBNull(4) ? "Studio user" : reader.GetString(4);
            string? userName = reader.IsDBNull(5) ? null : reader.GetString(5);
            return new EmailRouteResult(true,workspaceId,workspaceName,null,null,"Person",conversationId,userName,display);
        }

        return new EmailRouteResult(true,workspaceId,workspaceName,null,null,"Workspace",workspaceId,null,workspaceName);
    }

    public string PersonAddress(string userName)
    {
        string domain = configuration["Messaging:PersonAddressDomain"]?.Trim().TrimStart('@')
            ?? configuration["Messaging:AddressDomain"]?.Trim().TrimStart('@')
            ?? "bryanstudiosltd.com";
        string local = userName.Trim();
        int slash = Math.Max(local.LastIndexOf('\\'), local.LastIndexOf('/'));
        if (slash >= 0) local = local[(slash + 1)..];
        int at = local.IndexOf('@');
        if (at > 0) local = local[..at];
        local = Regex.Replace(local.ToLowerInvariant(), @"[^a-z0-9._-]+", ".");
        local = local.Trim('.');
        if (string.IsNullOrWhiteSpace(local)) local = "user-" + StableId(userName).ToString("N")[..8];
        return local + "@" + domain.ToLowerInvariant();
    }

    public string JobAddress(Guid jobId)
    {
        string domain = configuration["Messaging:PersonAddressDomain"]?.Trim().TrimStart('@')
            ?? configuration["Messaging:AddressDomain"]?.Trim().TrimStart('@')
            ?? "bryanstudiosltd.com";
        return $"job+{jobId:D}@{domain.ToLowerInvariant()}";
    }

    public static bool IsExternalEmail(string? value)
        => MailAddress.TryCreate(value?.Trim(), out MailAddress? address) && address.Address.Contains('@');

    public static string ReferenceToken(Guid conversationId)
        => "AEGIS-" + conversationId.ToString("N")[..8].ToUpperInvariant();

    private static string NormalizeAddress(string? value)
        => value?.Trim().Trim('<','>').ToLowerInvariant() ?? string.Empty;

    private static StudioMessageAddress Read(NpgsqlDataReader reader)
        => new(reader.GetString(0),reader.GetString(1),reader.GetString(2),
            reader.IsDBNull(3)?null:reader.GetGuid(3),reader.IsDBNull(4)?null:reader.GetGuid(4),
            reader.IsDBNull(5)?null:reader.GetString(5));

    private static Guid StableId(string value)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim().ToLowerInvariant()));
        return new Guid(bytes.AsSpan(0,16));
    }
}

public sealed record StudioMessageAddress(
    string Address,
    string DestinationType,
    string DisplayName,
    Guid? WorkspaceId,
    Guid? JobId,
    string? PersonUserName);
