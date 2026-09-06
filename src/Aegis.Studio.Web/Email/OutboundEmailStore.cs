using System.Collections.Concurrent;
using System.Text.Json;
using Aegis.Studio.Web.Demo;

namespace Aegis.Studio.Web.Email;

public sealed class OutboundEmailStore
{
    private readonly ConcurrentDictionary<Guid, OutboundEmail> messages = new();
    private readonly IReadOnlyDictionary<Guid, string> workspaceAddresses;
    private readonly object persistenceLock = new();
    private readonly string dataFile;

    public OutboundEmailStore(IWebHostEnvironment environment, IConfiguration configuration)
    {
        string dataDirectory = Path.Combine(environment.ContentRootPath, "App_Data");
        Directory.CreateDirectory(dataDirectory);
        dataFile = Path.Combine(dataDirectory, "email-outbox.json");

        List<WorkspaceEmailRoute> routes = configuration
            .GetSection("EmailRouting:Routes")
            .Get<List<WorkspaceEmailRoute>>() ?? new List<WorkspaceEmailRoute>();
        workspaceAddresses = routes
            .Where(x => x.WorkspaceId != Guid.Empty && !string.IsNullOrWhiteSpace(x.Address))
            .ToDictionary(x => x.WorkspaceId, x => x.Address, EqualityComparer<Guid>.Default);

        Load();
    }

    public OutboundEmail Queue(DemoConversation conversation, string body)
    {
        if (!workspaceAddresses.TryGetValue(conversation.WorkspaceId, out string? fromAddress) ||
            string.IsNullOrWhiteSpace(fromAddress))
        {
            throw new InvalidOperationException("The conversation workspace has no outbound email identity configured.");
        }

        OutboundEmail message = new(
            Guid.NewGuid(),
            conversation.Id,
            fromAddress,
            conversation.ContactAddress,
            conversation.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)
                ? conversation.Subject
                : "Re: " + conversation.Subject,
            body.Trim(),
            conversation.ExternalThreadId,
            "Pending",
            DateTimeOffset.UtcNow,
            null);

        messages[message.Id] = message;
        Save();
        return message;
    }

    public IReadOnlyList<OutboundEmail> GetPending()
    {
        return messages.Values
            .Where(x => string.Equals(x.Status, "Pending", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.CreatedAt)
            .ToList();
    }

    public OutboundEmail? MarkSent(Guid id, string? providerMessageId)
    {
        if (!messages.TryGetValue(id, out OutboundEmail? current))
        {
            return null;
        }

        OutboundEmail sent = current with
        {
            Status = "Sent",
            ProviderMessageId = string.IsNullOrWhiteSpace(providerMessageId) ? null : providerMessageId.Trim()
        };
        messages[id] = sent;
        Save();
        return sent;
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
            List<OutboundEmail>? stored = JsonSerializer.Deserialize<List<OutboundEmail>>(json);
            if (stored is null)
            {
                return;
            }

            foreach (OutboundEmail message in stored)
            {
                messages[message.Id] = message;
            }
        }
        catch (JsonException)
        {
            // Demo persistence corruption must not stop Studio from starting.
        }
    }

    private void Save()
    {
        lock (persistenceLock)
        {
            string json = JsonSerializer.Serialize(
                messages.Values.OrderBy(x => x.CreatedAt).ToList(),
                new JsonSerializerOptions { WriteIndented = true });
            string temporaryFile = dataFile + ".tmp";
            File.WriteAllText(temporaryFile, json);
            File.Move(temporaryFile, dataFile, true);
        }
    }
}

public sealed record OutboundEmail(
    Guid Id,
    Guid ConversationId,
    string FromAddress,
    string ToAddress,
    string Subject,
    string Body,
    string? ThreadId,
    string Status,
    DateTimeOffset CreatedAt,
    string? ProviderMessageId);

public sealed record MarkOutboundSentRequest(string? ProviderMessageId);
