using System.Collections.Concurrent;
using System.Text.Json;
using Aegis.Studio.Application.EmailRouting;

namespace Aegis.Studio.Web.Demo;

public sealed class DemoStore
{
    private static readonly Guid WeddingsWorkspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid IdPhotosWorkspaceId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid GraduationsWorkspaceId = Guid.Parse("55555555-5555-5555-5555-555555555555");

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
            new(WeddingsWorkspaceId, "Weddings", "weddings@bryanstudiosltd.com", conversations.Values.Count(x => x.WorkspaceId == WeddingsWorkspaceId && !x.IsClosed)),
            new(IdPhotosWorkspaceId, "ID Photos", "idphotos@bryanstudiosltd.com", conversations.Values.Count(x => x.WorkspaceId == IdPhotosWorkspaceId && !x.IsClosed)),
            new(GraduationsWorkspaceId, "Graduations", "graduations@bryanstudiosltd.com", conversations.Values.Count(x => x.WorkspaceId == GraduationsWorkspaceId && !x.IsClosed))
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

    public EmailIngestResult ReceiveEmail(InboundEmail email, EmailRouteResult route)
    {
        if (!route.IsRouted || !route.WorkspaceId.HasValue)
        {
            throw new InvalidOperationException("An email must be routed before it can be ingested.");
        }

        DemoConversation? duplicate = conversations.Values.FirstOrDefault(x =>
            x.Messages.Any(message => string.Equals(
                message.ExternalMessageId,
                email.MessageId,
                StringComparison.OrdinalIgnoreCase)));

        if (duplicate is not null)
        {
            return new EmailIngestResult(duplicate, false, true);
        }

        DemoConversation? existing = FindConversationForEmail(email, route.WorkspaceId.Value);
        DemoMessage message = new(
            Guid.NewGuid(),
            email.FromName.Trim(),
            "External",
            email.Body.Trim(),
            email.ReceivedAt,
            email.MessageId);

        if (existing is not null)
        {
            existing.Messages.Add(message);
            Save();
            return new EmailIngestResult(existing, false, false);
        }

        DemoConversation conversation = new(
            Guid.NewGuid(),
            route.WorkspaceId.Value,
            email.Subject.Trim(),
            email.FromName.Trim(),
            email.FromAddress.Trim(),
            "Email",
            false,
            null,
            new List<DemoMessage> { message },
            NormaliseThreadId(email.ThreadId));

        conversations[conversation.Id] = conversation;
        Save();
        return new EmailIngestResult(conversation, true, false);
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
            DateTimeOffset.UtcNow,
            null,
            new List<DemoFinancialEvent>());

        jobs[job.Id] = job;
        conversations[conversationId] = conversation with { JobId = job.Id };
        Save();
        return job;
    }

    public IReadOnlyList<DemoJob> GetJobs()
    {
        return jobs.Values.OrderByDescending(x => x.CreatedAt).ToList();
    }

    public DemoJob? GetJob(Guid id)
    {
        jobs.TryGetValue(id, out DemoJob? job);
        return job;
    }

    public DemoJob UpdateJob(Guid id, UpdateJobRequest request)
    {
        DemoJob current = jobs[id];
        DemoJob updated = current with
        {
            Status = string.IsNullOrWhiteSpace(request.Status) ? current.Status : request.Status.Trim(),
            AssignedTo = string.IsNullOrWhiteSpace(request.AssignedTo) ? current.AssignedTo : request.AssignedTo.Trim(),
            EventDate = request.EventDate ?? current.EventDate
        };
        jobs[id] = updated;
        Save();
        return updated;
    }

    public DemoJob AddFinancialEvent(Guid id, AddFinancialEventRequest request)
    {
        DemoJob current = jobs[id];
        List<DemoFinancialEvent> events = current.FinancialEvents?.ToList() ?? new List<DemoFinancialEvent>();
        events.Add(new DemoFinancialEvent(
            Guid.NewGuid(),
            request.Kind.Trim(),
            request.Amount,
            request.Note?.Trim(),
            DateTimeOffset.UtcNow));
        DemoJob updated = current with { FinancialEvents = events };
        jobs[id] = updated;
        Save();
        return updated;
    }

    public AccountsSummary GetAccountsSummary()
    {
        decimal total = jobs.Values.Sum(x => x.FinancialEvents?.Sum(e => e.Amount) ?? 0m);
        int jobsWithActivity = jobs.Values.Count(x => (x.FinancialEvents?.Count ?? 0) > 0);
        return new AccountsSummary(total, jobsWithActivity, jobs.Count);
    }

    private DemoConversation? FindConversationForEmail(InboundEmail email, Guid workspaceId)
    {
        string? threadId = NormaliseThreadId(email.ThreadId);
        if (threadId is not null)
        {
            DemoConversation? threaded = conversations.Values.FirstOrDefault(x =>
                x.WorkspaceId == workspaceId &&
                string.Equals(x.ExternalThreadId, threadId, StringComparison.OrdinalIgnoreCase));

            if (threaded is not null)
            {
                return threaded;
            }
        }

        string normalisedSubject = NormaliseSubject(email.Subject);
        return conversations.Values
            .Where(x => x.WorkspaceId == workspaceId)
            .Where(x => string.Equals(x.ContactAddress, email.FromAddress, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(x => string.Equals(
                NormaliseSubject(x.Subject),
                normalisedSubject,
                StringComparison.OrdinalIgnoreCase));
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
                jobs[job.Id] = job with { FinancialEvents = job.FinancialEvents ?? new List<DemoFinancialEvent>() };
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

        DemoConversation idPhoto = new(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            IdPhotosWorkspaceId,
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
        conversations[idPhoto.Id] = idPhoto;
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
            return IdPhotosWorkspaceId;
        }

        return GraduationsWorkspaceId;
    }

    private static string NormaliseChannel(string? channel)
    {
        string? value = channel?.Trim();
        return string.IsNullOrWhiteSpace(value) ? "Manual" : value;
    }

    private static string? NormaliseThreadId(string? threadId)
    {
        return string.IsNullOrWhiteSpace(threadId) ? null : threadId.Trim();
    }

    private static string NormaliseSubject(string subject)
    {
        string value = subject.Trim();
        while (value.StartsWith("RE:", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("FW:", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("FWD:", StringComparison.OrdinalIgnoreCase))
        {
            int separator = value.IndexOf(':');
            value = value[(separator + 1)..].Trim();
        }

        return value;
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
    List<DemoMessage> Messages,
    string? ExternalThreadId = null);

public sealed record DemoMessage(
    Guid Id,
    string Author,
    string Visibility,
    string Body,
    DateTimeOffset CreatedAt,
    string? ExternalMessageId = null);

public sealed record DemoJob(
    Guid Id,
    Guid WorkspaceId,
    Guid ConversationId,
    string Title,
    string ClientName,
    string Status,
    DateOnly? EventDate,
    DateTimeOffset CreatedAt,
    string? AssignedTo = null,
    List<DemoFinancialEvent>? FinancialEvents = null);

public sealed record DemoFinancialEvent(Guid Id, string Kind, decimal Amount, string? Note, DateTimeOffset CreatedAt);

public sealed record DemoState(List<DemoConversation> Conversations, List<DemoJob> Jobs);

public sealed record EmailIngestResult(DemoConversation Conversation, bool CreatedConversation, bool WasDuplicate);

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

public sealed record UpdateJobRequest(string? Status, string? AssignedTo, DateOnly? EventDate);

public sealed record AddFinancialEventRequest(string Kind, decimal Amount, string? Note);

public sealed record AccountsSummary(decimal RecordedAmount, int JobsWithFinancialActivity, int TotalJobs);
