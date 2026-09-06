using System.Text.Json;
using Aegis.Studio.Application.EmailRouting;

namespace Aegis.Studio.Web.Email;

public sealed class UnroutedEmailStore
{
    private readonly object syncRoot = new();
    private readonly string dataFile;
    private readonly List<UnroutedEmail> items = new();

    public UnroutedEmailStore(IWebHostEnvironment environment)
    {
        string dataDirectory = Path.Combine(environment.ContentRootPath, "App_Data");
        Directory.CreateDirectory(dataDirectory);
        dataFile = Path.Combine(dataDirectory, "unrouted-email.json");
        Load();
    }

    public IReadOnlyList<UnroutedEmail> GetOpen()
    {
        lock (syncRoot)
        {
            return items
                .Where(x => !x.ResolvedAt.HasValue)
                .OrderByDescending(x => x.ReceivedAt)
                .ToList();
        }
    }

    public UnroutedEmail Capture(InboundEmail email, string reason)
    {
        lock (syncRoot)
        {
            UnroutedEmail? existing = items.FirstOrDefault(x =>
                string.Equals(x.ProviderMessageId, email.MessageId, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return existing;
            }

            UnroutedEmail item = new(
                Guid.NewGuid(),
                email.MessageId,
                email.ThreadId,
                email.FromName,
                email.FromAddress,
                email.ToAddresses.ToList(),
                email.CcAddresses.ToList(),
                new Dictionary<string, string>(email.InternetHeaders, StringComparer.OrdinalIgnoreCase),
                email.Subject,
                email.Body,
                email.ReceivedAt,
                reason,
                null,
                null,
                null);

            items.Add(item);
            Save();
            return item;
        }
    }

    public UnroutedEmail? Get(Guid id)
    {
        lock (syncRoot)
        {
            return items.FirstOrDefault(x => x.Id == id);
        }
    }

    public UnroutedEmail? MarkResolved(Guid id, Guid workspaceId, string workspaceName)
    {
        lock (syncRoot)
        {
            int index = items.FindIndex(x => x.Id == id);
            if (index < 0)
            {
                return null;
            }

            UnroutedEmail current = items[index];
            UnroutedEmail updated = current with
            {
                ResolvedAt = DateTimeOffset.UtcNow,
                ResolvedWorkspaceId = workspaceId,
                ResolvedWorkspaceName = workspaceName
            };
            items[index] = updated;
            Save();
            return updated;
        }
    }

    private void Load()
    {
        if (!File.Exists(dataFile))
        {
            return;
        }

        try
        {
            string json = File.ReadAllText(dataFile);
            List<UnroutedEmail>? loaded = JsonSerializer.Deserialize<List<UnroutedEmail>>(json);
            if (loaded is not null)
            {
                items.AddRange(loaded);
            }
        }
        catch (JsonException)
        {
        }
    }

    private void Save()
    {
        string json = JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true });
        string temporaryFile = dataFile + ".tmp";
        File.WriteAllText(temporaryFile, json);
        File.Move(temporaryFile, dataFile, true);
    }
}

public sealed record UnroutedEmail(
    Guid Id,
    string ProviderMessageId,
    string? ThreadId,
    string FromName,
    string FromAddress,
    List<string> ToAddresses,
    List<string> CcAddresses,
    Dictionary<string, string> InternetHeaders,
    string Subject,
    string Body,
    DateTimeOffset ReceivedAt,
    string FailureReason,
    DateTimeOffset? ResolvedAt,
    Guid? ResolvedWorkspaceId,
    string? ResolvedWorkspaceName)
{
    public InboundEmail ToInboundEmail()
    {
        return new InboundEmail(
            ProviderMessageId,
            ThreadId,
            FromName,
            FromAddress,
            ToAddresses,
            CcAddresses,
            InternetHeaders,
            Subject,
            Body,
            ReceivedAt);
    }
}

public sealed record ResolveUnroutedEmailRequest(Guid WorkspaceId);
