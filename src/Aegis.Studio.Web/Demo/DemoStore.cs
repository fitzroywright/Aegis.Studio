using System.Collections.Concurrent;

namespace Aegis.Studio.Web.Demo;

public sealed class DemoStore
{
    private readonly ConcurrentDictionary<Guid, DemoConversation> conversations = new();
    private readonly ConcurrentDictionary<Guid, DemoJob> jobs = new();

    public DemoStore()
    {
        Guid workspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        DemoConversation wedding = new(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            workspaceId,
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
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
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

    public IReadOnlyList<DemoWorkspace> GetWorkspaces()
    {
        return new List<DemoWorkspace>
        {
            new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Weddings", "weddings@bryans.com", conversations.Values.Count(x => x.WorkspaceId == Guid.Parse("11111111-1111-1111-1111-111111111111") && !x.IsClosed)),
            new(Guid.Parse("44444444-4444-4444-4444-444444444444"), "Schools & IDs", "ids@bryans.com", conversations.Values.Count(x => x.WorkspaceId == Guid.Parse("44444444-4444-4444-4444-444444444444") && !x.IsClosed)),
            new(Guid.Parse("55555555-5555-5555-5555-555555555555"), "Portraits", "portraits@bryans.com", 0)
        };
    }

    public IReadOnlyList<DemoConversation> GetConversations(Guid? workspaceId)
    {
        IEnumerable<DemoConversation> query = conversations.Values;
        if (workspaceId.HasValue)
        {
            query = query.Where(x => x.WorkspaceId == workspaceId.Value);
        }

        return query.OrderByDescending(x => x.Messages.Max(m => m.CreatedAt)).ToList();
    }

    public DemoConversation? GetConversation(Guid id)
    {
        conversations.TryGetValue(id, out DemoConversation? conversation);
        return conversation;
    }

    public DemoConversation AddMessage(Guid id, AddMessageRequest request)
    {
        DemoConversation conversation = conversations[id];
        string visibility = request.IsInternal ? "Internal" : "External";
        string author = request.IsInternal ? "Aegis Staff" : "Bryan's Studio";
        conversation.Messages.Add(new DemoMessage(Guid.NewGuid(), author, visibility, request.Body.Trim(), DateTimeOffset.UtcNow));
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
        return job;
    }

    public IReadOnlyList<DemoJob> GetJobs()
    {
        return jobs.Values.OrderByDescending(x => x.CreatedAt).ToList();
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

public sealed record AddMessageRequest(string Body, bool IsInternal);

public sealed record CreateJobRequest(string Title, DateOnly? EventDate);
