using System.Collections.Concurrent;
using System.Text.Json;

namespace Aegis.Studio.Web.Demo;

public sealed class DemoStore
{
    private static readonly Guid WeddingsWorkspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SchoolsWorkspaceId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid PortraitsWorkspaceId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private readonly ConcurrentDictionary<Guid, DemoConversation> conversations = new();
    private readonly ConcurrentDictionary<Guid, DemoJob> jobs = new();
    private readonly object persistenceLock = new();
    private readonly string dataFile;

    public DemoStore(IWebHostEnvironment environment)
    {
        string dataDirectory = Path.Combine(environment.ContentRootPath, "App_Data");
        Directory.CreateDirectory(dataDirectory);
        dataFile = Path.Combine(dataDirectory, "studio-demo.json");

        if (!Load())
        {
            Seed();
            Save();
        }
    }

    public IReadOnlyList<DemoWorkspace> GetWorkspaces()
    {
        return new List<DemoWorkspace>
        {
            new(WeddingsWorkspaceId, "Weddings", "weddings@bryans.com", conversations.Values.Count(x => x.WorkspaceId == WeddingsWorkspaceId && !x.IsClosed)),
            new(SchoolsWorkspaceId, "Schools & IDs", "ids@bryans.com", conversations.Values.Count(x => x.WorkspaceId == SchoolsWorkspaceId && !x.IsClosed)),
            new(PortraitsWorkspaceId, "Portraits", "portraits@bryans.com", conversations.Values.Count(x => x.WorkspaceId == PortraitsWorkspaceId && !x.IsClosed))
        };
    }

    public IReadOnlyList<DemoConversation> GetConversations(Guid? workspaceId)
    {
        IEnumerable<DemoConversation> query = conversations.Values.Where(x => !x.IsClosed);
        if (workspaceId.HasValue)
        {
            query = query.Where(x => x.WorkspaceId == workspaceId.Value);
        }

        return query.OrderByDescending(GetLastActivity).ToList();
    }

    public DemoConversation? GetConversation(Guid id)
    {
        conversations.TryGetValue(id, out DemoConversation? conversation);
        return conversation;
    }

    public DemoConversation ReceiveInteraction(IncomingInteractionRequest request)
    {
        Guid workspaceId = ResolveWorkspace(request.WorkspaceId, request.Destination);
        DemoConversation conversation = new(
            Guid.NewGuid(),
            workspaceId,
            request.Subject.Trim(),
            request.ContactName.Trim(),
            request.ContactAddress.Trim(),
            NormaliseChannel(request.Channel),
            false,
            null,
            new List<DemoMessage>
            {
                new(Guid.NewGuid(), request.ContactName.Trim(), "External", request.Body.Trim(), DateTimeOffset.UtcNow)
            });

        conversations[conversation.Id] = conversation;
        Save();
        return conversation;
    }

    public DemoConversation AddMessage(Guid id, AddMessageRequest request)
    {
        DemoConversation conversation = conversations[id];
        string visibility = request.IsInternal ? "Internal" : "External";
        string author = request.IsInternal ? "Aegis Staff" : "Bryan's Studio";
        conversation.Messages.Add(new DemoMessage(Guid.NewGuid(), author, visibility, request.Body.Trim(), DateTimeOffset.UtcNow));
        Save();
        return conversation;
    }

    public DemoJob CreateJob(Guid conversationId, CreateJobRequest request)
    {
        DemoConversation conversation = conversations[conversationId];
        if (conversation.JobId.HasValue && jobs.TryGetValue(conversation.JobId.Value, out DemoJob? existing))
        {
            return existing;
        }

        DemoJob job = new(
            Guid.NewGuid(),
            conversation.WorkspaceId,
            conversation.Id,
            request.Title.Trim(),
            conversation.ContactName,
            "New",
            request.EventDate,
            DateTimeOffset.UtcNow);

        jobs[job.Id] = job;
        conversations[conversationId] = conversation with { JobId = job.Id };
        Save();
        return job;
    }

    public IReadOnlyList<DemoJob> GetJobs()
    {
        return jobs.Values.OrderByDescending(x => x.CreatedAt).ToList();
    }

    private bool Load()
    {
        if (!File.Exists(dataFile))
        {
            return false;
        }

        try
        {
            string json = File.ReadAllText(dataFile);
            DemoState? state = JsonSerializer.Deserialize<DemoState>(json);
            if (state is null)
            {
                return false;
            }

            foreach (DemoConversation conversation in state.Conversations)
            {
                conversations[conversation.Id] = conversation;
            }

            foreach (DemoJob job in state.Jobs)
            {
                jobs[job.Id] = job;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void Save()
    {
        lock (persistenceLock)
        {
            DemoState state = new(conversations.Values.ToList(), jobs.Values.ToList());
            string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            string temporaryFile = dataFile + ".tmp";
            File.WriteAllText(temporaryFile, json);
            File.Move(temporaryFile, dataFile, true);
        }
    }

    private void Seed()
    {
        DemoConversation wedding = new(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            WeddingsWorkspaceId,
            "Johnson Wedding - October 18",
            "Marsha Johnson",
            "marsha.johnson@example.com",
            "Email",
            false,
            null,
            new List<DemoMessage>
            {
                new(Guid.NewGuid(), "Marsha Johnson", "External", "Hi Bryan's, I would like pricing and availability for our wedding on October 18.", DateTimeOffset.UtcNow.AddHours(-4)),
                new(Guid.NewGuid(), "Keisha - Front Desk", "Internal", "October 18 is still open. Wedding package sheet sent last month should still be current.", DateTimeOffset.UtcNow.AddHours(-3)),
                new(Guid.NewGuid(), "Bryan's Weddings", "External", "Thanks Marsha. We are available. I can put together the package options and hold the date while we talk.", DateTimeOffset.UtcNow.AddHours(-2))
            });
        conversations[wedding.Id] = wedding;

        DemoConversation school = new(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            SchoolsWorkspaceId,
            "Grade 11 ID photo correction",
            "St. Catherine Academy",
            "office@example.edu",
            "Email",
            false,
            null,
            new List<DemoMessage>
            {
                new(Guid.NewGuid(), "School Office", "External", "One student's surname is incorrect on the ID proof. Can production correct it before printing?", DateTimeOffset.UtcNow.AddHours(-1))
            });
        conversations[school.Id] = school;
    }

    private static DateTimeOffset GetLastActivity(DemoConversation conversation)
    {
        return conversation.Messages.Count == 0 ? DateTimeOffset.MinValue : conversation.Messages.Max(x => x.CreatedAt);
    }

    private static Guid ResolveWorkspace(Guid? workspaceId, string? destination)
    {
        if (workspaceId.HasValue)
        {
            return workspaceId.Value;
        }

        string value = destination?.Trim().ToLowerInvariant() ?? string.Empty;
        if (value.Contains("wedding", StringComparison.Ordinal))
        {
            return WeddingsWorkspaceId;
        }

        if (value.Contains("id", StringComparison.Ordinal) || value.Contains("school", StringComparison.Ordinal))
        {
            return SchoolsWorkspaceId;
        }

        return PortraitsWorkspaceId;
    }

    private static string NormaliseChannel(string? channel)
    {
        string value = channel?.Trim();
        return string.IsNullOrWhiteSpace(value) ? "Manual" : value;
    }
}

public sealed record DemoWorkspace(Guid Id, string Name, string Address, int OpenCount);

public sealed record DemoConversation(
    Guid Id,
    Guid WorkspaceId,
    string Subject,
    string ContactName,
    string ContactAddress,
    string Channel,
    bool IsClosed,
    Guid? JobId,
    List<DemoMessage> Messages);

public sealed record DemoMessage(Guid Id, string Author, string Visibility, string Body, DateTimeOffset CreatedAt);

public sealed record DemoJob(
    Guid Id,
    Guid WorkspaceId,
    Guid ConversationId,
    string Title,
    string ClientName,
    string Status,
    DateOnly? EventDate,
    DateTimeOffset CreatedAt);

public sealed record DemoState(List<DemoConversation> Conversations, List<DemoJob> Jobs);

public sealed record IncomingInteractionRequest(
    Guid? WorkspaceId,
    string? Destination,
    string ContactName,
    string ContactAddress,
    string Subject,
    string Body,
    string? Channel);

public sealed record AddMessageRequest(string Body, bool IsInternal);

public sealed record CreateJobRequest(string Title, DateOnly? EventDate);
