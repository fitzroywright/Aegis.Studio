using System.Net.Mail;
using System.Security.Claims;
using System.Text.Json;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Profiles;
using Npgsql;

namespace Aegis.Studio.Web.Messaging;

public sealed record StudioAddressEntry(Guid Id, string DisplayName, string EmailAddress, string ContactType,
    string InboxType, string DeliveryMode, string? StudioInboxId = null, string? ProviderReference = null,
    string Status = "Active", string? UserName = null, Guid? ScopeId = null,
    bool StudioManagedGroup = false, bool GroupVerified = false, string? SenderMailbox = null,
    bool SenderVerified = false, string CombinedMailboxPolicy = "ExternalCopy", string? VerificationEvidence = null);

public sealed class StudioAddressBook
{
    private readonly StudioDatabase database;
    private readonly StudioEmployeeDirectoryStore employees;
    private readonly UserProfileStore profiles;
    private readonly WorkspaceStore workspaces;
    private readonly WorkspaceAccessService workspaceAccess;
    private readonly JobAccessService jobAccess;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public StudioAddressBook(StudioDatabase database, UserProfileStore profiles, WorkspaceStore workspaces,
        WorkspaceAccessService workspaceAccess, JobAccessService jobAccess, StudioEmployeeDirectoryStore employees)
    { this.database = database; this.profiles = profiles; this.workspaces = workspaces; this.workspaceAccess = workspaceAccess; this.jobAccess = jobAccess; this.employees = employees; }

    // Source identities stay in their existing stores. Only routing configuration and external contacts are persisted here.
    public IReadOnlyList<StudioAddressEntry> GetAll(ClaimsPrincipal? user = null)
    {
        Dictionary<Guid, StudioAddressEntry> entries = profiles.GetAll().ToDictionary(p => p.Id,
            p => new StudioAddressEntry(p.Id, p.DisplayName, p.Email ?? "", "Staff", "Internal", "InternalOnly", p.UserName, UserName: p.UserName));
        foreach (var employee in employees.GetAll())
        {
            Guid id=UserProfileStore.StableProfileId(employee.UserName);
            if (!entries.ContainsKey(id)) entries[id]=new(id,employee.DisplayName,employee.EmailAddress??"","Staff","Internal","InternalOnly",employee.UserName,UserName:employee.UserName,Status:employee.IsActive?"Active":"Inactive");
            else if (!employee.IsActive) entries[id]=entries[id] with { Status="Inactive" };
        }
        foreach (var w in workspaces.GetAll(true)) entries[w.Id] = new(w.Id, w.Name, w.EmailAddress ?? "", "Workspace", "Internal", "InternalOnly",
            w.Id.ToString("D"), Status: w.IsEnabled ? "Active" : "Inactive", ScopeId: w.Id);
        using var c = database.OpenConnection();
        using (var q = new NpgsqlCommand("select id,display_name,coalesce(email,'') from studio_clients", c))
        using (var r = q.ExecuteReader())
            while (r.Read()) entries[r.GetGuid(0)] = new(r.GetGuid(0), r.GetString(1), r.GetString(2), "Client", "External", "ExternalOnly");
        using (var q = new NpgsqlCommand("select id,title from studio_jobs", c))
        using (var r = q.ExecuteReader())
            while (r.Read()) entries[r.GetGuid(0)] = new(r.GetGuid(0), r.GetString(1), "", "Job", "Internal", "InternalOnly", r.GetGuid(0).ToString("D"), ScopeId: r.GetGuid(0));
        using (var q = new NpgsqlCommand("select id,payload::text from studio_address_routes", c))
        using (var r = q.ExecuteReader())
            while (r.Read())
            {
                var configured = JsonSerializer.Deserialize<StudioAddressEntry>(r.GetString(1), Json)!;
                if (configured.ContactType == "ExternalContact") entries[configured.Id] = configured;
                else if (entries.TryGetValue(configured.Id, out var source))
                    entries[configured.Id] = configured with { DisplayName = source.DisplayName, UserName = source.UserName, ScopeId = source.ScopeId,
                        Status = source.Status == "Inactive" ? "Inactive" : configured.Status };
            }
        return entries.Values.Where(e => user is null || (e.ContactType switch
        {
            "Job" => e.ScopeId.HasValue && jobAccess.HasPermission(user, e.ScopeId.Value, "ViewJob"),
            "Workspace" => e.ScopeId.HasValue && workspaceAccess.HasPermission(user, e.ScopeId.Value, "View"),
            _ => true
        })).OrderBy(e => e.DisplayName).ToArray();
    }

    // Messaging can target a subject's existing contact email, but inbound address resolution
    // intentionally continues to use GetAll/Resolve so subject addresses are not mailbox routes.
    public IReadOnlyList<StudioAddressEntry> GetMessagingRecipients(ClaimsPrincipal? user = null)
    {
        List<StudioAddressEntry> entries = GetAll(user).ToList();
        using var connection = database.OpenConnection();
        using var command = new NpgsqlCommand("""
            select s.id,s.name,s.contact_email,s.job_id,j.title
            from studio_job_subjects s
            join studio_jobs j on j.id=s.job_id
            where not s.is_system and nullif(btrim(s.contact_email),'') is not null
            order by s.name,j.title,s.id
            """, connection);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            Guid jobId = reader.GetGuid(3);
            if (user is not null && !jobAccess.HasPermission(user, jobId, "ReplyRequester")) continue;
            string rawEmail = reader.GetString(2);
            string email = rawEmail;
            string status = "Active";
            try { email = ValidateAddress(rawEmail); }
            catch (InvalidOperationException) { status = "Attention"; }
            entries.Add(new StudioAddressEntry(
                reader.GetGuid(0),
                reader.GetString(1),
                email,
                "Subject",
                "External",
                "ExternalOnly",
                ProviderReference: reader.GetString(4),
                Status: status,
                ScopeId: jobId));
        }
        return entries.OrderBy(e => e.DisplayName).ThenBy(e => e.ContactType).ToArray();
    }

    public StudioAddressEntry Get(Guid id, ClaimsPrincipal? user = null)
        => GetMessagingRecipients(user).FirstOrDefault(e => e.Id == id) ?? throw new KeyNotFoundException("Address book destination was not found.");

    public StudioAddressEntry? Resolve(string address)
    {
        var matches = GetAll().Where(e => Normalize(e.EmailAddress) == Normalize(address) && e.Status == "Active").ToArray();
        if (matches.Length > 1) throw new InvalidOperationException("The address matches multiple Studio identities; review routing configuration.");
        return matches.SingleOrDefault();
    }

    public StudioAddressEntry GetOrCreateExternal(string name,string address)
    {
        string email=ValidateAddress(address);var known=Resolve(email);if(known is not null)return known;
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        StudioUnifiedMessaging.LockRequest(c,tx,"contact:"+email);
        known=Resolve(email);if(known is not null){tx.Commit();return known;}
        var entry=new StudioAddressEntry(Guid.NewGuid(),string.IsNullOrWhiteSpace(name)?email:name.Trim(),email,"ExternalContact","External","ExternalOnly");
        using var q=new NpgsqlCommand("insert into studio_address_routes(id,email_address,payload) values($1,$2,$3::jsonb)",c,tx);
        StudioUnifiedMessaging.Add(q,entry.Id,email,JsonSerializer.Serialize(entry,Json));q.ExecuteNonQuery();tx.Commit();return entry;
    }

    public StudioAddressEntry Save(StudioAddressEntry entry)
    {
        if (entry.Id == Guid.Empty || string.IsNullOrWhiteSpace(entry.DisplayName)) throw new InvalidOperationException("An identity and display name are required.");
        if (!new[] { "Staff", "Client", "ExternalContact", "Workspace", "Job" }.Contains(entry.ContactType)
            || !new[] { "Internal", "Microsoft365", "External" }.Contains(entry.InboxType)
            || !new[] { "InternalOnly", "ExternalOnly", "Combined" }.Contains(entry.DeliveryMode)
            || !new[] { "Active", "Inactive", "Attention" }.Contains(entry.Status)
            || !new[] { "ExternalCopy", "InternalOnly", "Hold" }.Contains(entry.CombinedMailboxPolicy))
            throw new InvalidOperationException("Invalid address routing options.");
        var existing = GetAll();
        if (entry.ContactType != "ExternalContact")
        {
            var source = existing.FirstOrDefault(e => e.Id == entry.Id) ?? throw new InvalidOperationException("Register an existing Studio identity.");
            if (source.ContactType != entry.ContactType || source.UserName != entry.UserName || source.ScopeId != entry.ScopeId)
                throw new InvalidOperationException("Routing configuration cannot change a source identity.");
        }
        if (!string.IsNullOrWhiteSpace(entry.EmailAddress)) entry = entry with { EmailAddress = ValidateAddress(entry.EmailAddress) };
        if (entry.DeliveryMode != "InternalOnly" && string.IsNullOrWhiteSpace(entry.EmailAddress)) throw new InvalidOperationException("External delivery requires an email address.");
        if (entry.DeliveryMode != "ExternalOnly" && (entry.ContactType is "Client" or "ExternalContact" || string.IsNullOrWhiteSpace(entry.StudioInboxId)))
            throw new InvalidOperationException("An explicitly associated staff, workspace or job inbox is required for internal delivery.");
        string? expectedInbox = entry.ContactType == "Staff" ? entry.UserName : entry.ScopeId?.ToString("D");
        if (entry.StudioInboxId is not null && entry.StudioInboxId != expectedInbox) throw new InvalidOperationException("The Studio inbox must belong to this identity.");
        if (existing.Any(e => e.Id != entry.Id && !string.IsNullOrWhiteSpace(e.EmailAddress) && Normalize(e.EmailAddress) == Normalize(entry.EmailAddress)))
            throw new InvalidOperationException("This email address already belongs to another Studio contact.");
        if (entry.StudioManagedGroup && (entry.InboxType != "Microsoft365" || !Guid.TryParse(entry.ProviderReference, out _)))
            throw new InvalidOperationException("A Studio-managed group requires a Microsoft 365 group object ID.");
        if ((entry.GroupVerified || entry.SenderVerified) && string.IsNullOrWhiteSpace(entry.VerificationEvidence))
            throw new InvalidOperationException("Record the tenant validation evidence before enabling verified routing or sending.");
        if (entry.SenderVerified && (entry.ContactType != "Staff" || string.IsNullOrWhiteSpace(entry.SenderMailbox)))
            throw new InvalidOperationException("Verified sending requires a staff identity and authorized sending mailbox.");
        if (entry.SenderMailbox is not null) entry = entry with { SenderMailbox = ValidateAddress(entry.SenderMailbox) };
        using var c = database.OpenConnection();
        using var q = new NpgsqlCommand("insert into studio_address_routes(id,email_address,payload) values($1,$2,$3::jsonb) on conflict(id) do update set email_address=excluded.email_address,payload=excluded.payload", c);
        q.Parameters.AddWithValue(entry.Id); q.Parameters.AddWithValue(Normalize(entry.EmailAddress)); q.Parameters.AddWithValue(JsonSerializer.Serialize(entry, Json)); q.ExecuteNonQuery();
        return entry;
    }

    public static string Normalize(string? address) => address?.Trim().ToLowerInvariant() ?? "";
    public static string ValidateAddress(string value)
    {
        if (value.Contains('\r') || value.Contains('\n')) throw new InvalidOperationException("Invalid email address.");
        try { var address = new MailAddress(value.Trim()); if (address.Address != value.Trim() || !address.Address.Contains('@')) throw new FormatException(); return address.Address.ToLowerInvariant(); }
        catch (FormatException) { throw new InvalidOperationException("Supply a valid email address without a display name."); }
    }
}
