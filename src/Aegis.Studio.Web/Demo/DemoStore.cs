using Aegis.Studio.Application.EmailRouting;
using Aegis.Studio.Web.Persistence;

namespace Aegis.Studio.Web.Demo;

public sealed class DemoStore
{
    private const string ConversationsCollection = "conversations";
    private const string JobsCollection = "jobs";
    private static readonly Guid WeddingsWorkspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid IdPhotosWorkspaceId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid GraduationsWorkspaceId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private readonly PostgresDocumentStore persistence;

    public DemoStore(PostgresDocumentStore persistence)
    {
        this.persistence = persistence;
        SeedIfEmpty();
    }

    public IReadOnlyList<DemoWorkspace> GetWorkspaces()
    {
        IReadOnlyList<DemoConversation> conversations = persistence.GetAll<DemoConversation>(ConversationsCollection);
        return
        [
            new(WeddingsWorkspaceId, "Weddings", "weddings@bryanstudiosltd.com", conversations.Count(x => x.WorkspaceId == WeddingsWorkspaceId && !x.IsClosed)),
            new(IdPhotosWorkspaceId, "ID Photos", "idphotos@bryanstudiosltd.com", conversations.Count(x => x.WorkspaceId == IdPhotosWorkspaceId && !x.IsClosed)),
            new(GraduationsWorkspaceId, "Graduations", "graduations@bryanstudiosltd.com", conversations.Count(x => x.WorkspaceId == GraduationsWorkspaceId && !x.IsClosed))
        ];
    }

    public IReadOnlyList<DemoConversation> GetConversations(Guid? workspaceId)
    {
        IEnumerable<DemoConversation> query = persistence.GetAll<DemoConversation>(ConversationsCollection).Where(x => !x.IsClosed);
        if (workspaceId.HasValue)
        {
            query = query.Where(x => x.WorkspaceId == workspaceId.Value);
        }
        return query.OrderByDescending(GetLastActivity).ToList();
    }

    public DemoConversation? GetConversation(Guid id) => persistence.Get<DemoConversation>(ConversationsCollection, id);
    public DemoJob? GetJob(Guid id) => persistence.Get<DemoJob>(JobsCollection, id);
    public IReadOnlyList<DemoJob> GetJobs() => persistence.GetAll<DemoJob>(JobsCollection).OrderByDescending(x => x.CreatedAt).ToList();

    public DemoConversation ReceiveInteraction(IncomingInteractionRequest request)
    {
        Guid workspaceId = ResolveWorkspace(request.WorkspaceId, request.Destination);
        DemoConversation conversation = new(Guid.NewGuid(), workspaceId, request.Subject.Trim(), request.ContactName.Trim(), request.ContactAddress.Trim(), NormaliseChannel(request.Channel), false, null,
            [new(Guid.NewGuid(), request.ContactName.Trim(), "External", request.Body.Trim(), DateTimeOffset.UtcNow)]);
        persistence.Upsert(ConversationsCollection, conversation.Id, conversation);
        return conversation;
    }

    public EmailIngestResult ReceiveEmail(InboundEmail email, EmailRouteResult route)
    {
        if (!route.IsRouted || !route.WorkspaceId.HasValue)
        {
            throw new InvalidOperationException("An email must be routed before it can be ingested.");
        }

        List<DemoConversation> conversations = persistence.GetAll<DemoConversation>(ConversationsCollection).ToList();
        DemoConversation? duplicate = conversations.FirstOrDefault(x => x.Messages.Any(m => string.Equals(m.ExternalMessageId, email.MessageId, StringComparison.OrdinalIgnoreCase)));
        if (duplicate is not null)
        {
            return new EmailIngestResult(duplicate, false, true);
        }

        DemoConversation? existing = FindConversationForEmail(email, route.WorkspaceId.Value, conversations);
        DemoMessage message = new(Guid.NewGuid(), email.FromName.Trim(), "External", email.Body.Trim(), email.ReceivedAt.ToUniversalTime(), email.MessageId);
        if (existing is not null)
        {
            existing.Messages.Add(message);
            persistence.Upsert(ConversationsCollection, existing.Id, existing);
            return new EmailIngestResult(existing, false, false);
        }

        DemoConversation conversation = new(Guid.NewGuid(), route.WorkspaceId.Value, email.Subject.Trim(), email.FromName.Trim(), email.FromAddress.Trim(), "Email", false, null, [message], NormaliseThreadId(email.ThreadId));
        persistence.Upsert(ConversationsCollection, conversation.Id, conversation);
        return new EmailIngestResult(conversation, true, false);
    }

    public DemoConversation AddMessage(Guid id, AddMessageRequest request)
    {
        DemoConversation conversation = GetConversation(id) ?? throw new KeyNotFoundException("Conversation was not found.");
        conversation.Messages.Add(new DemoMessage(Guid.NewGuid(), request.IsInternal ? "Aegis Staff" : "Bryan's Studio", request.IsInternal ? "Internal" : "External", request.Body.Trim(), DateTimeOffset.UtcNow));
        persistence.Upsert(ConversationsCollection, conversation.Id, conversation);
        return conversation;
    }

    public DemoJob CreateJob(Guid conversationId, CreateJobRequest request)
    {
        DemoConversation conversation = GetConversation(conversationId) ?? throw new KeyNotFoundException("Conversation was not found.");
        if (conversation.JobId.HasValue)
        {
            DemoJob? existing = GetJob(conversation.JobId.Value);
            if (existing is not null)
            {
                return existing;
            }
        }

        DemoJob job = new(Guid.NewGuid(), conversation.WorkspaceId, conversation.Id, request.Title.Trim(), conversation.ContactName, "New", request.EventDate, DateTimeOffset.UtcNow, null, []);
        persistence.Upsert(JobsCollection, job.Id, job);
        DemoConversation updated = conversation with { JobId = job.Id };
        persistence.Upsert(ConversationsCollection, updated.Id, updated);
        return job;
    }

    public DemoJob CreateBookingJob(CreateBookingIntakeRequest request)
    {
        Guid workspaceId = ResolveWorkspace(request.WorkspaceId, request.Destination ?? request.SessionType);
        string channel = NormaliseChannel(request.Source);
        string address = !string.IsNullOrWhiteSpace(request.ClientEmail) ? request.ClientEmail.Trim() : request.ClientPhone?.Trim() ?? "Walk-in";
        string note = string.IsNullOrWhiteSpace(request.Notes) ? $"Booking request for {request.SessionType}." : request.Notes.Trim();
        DemoConversation conversation = new(Guid.NewGuid(), workspaceId, $"{request.SessionType} booking", request.ClientName.Trim(), address, channel, false, null,
            [new(Guid.NewGuid(), request.ClientName.Trim(), "External", note, DateTimeOffset.UtcNow)]);
        persistence.Upsert(ConversationsCollection, conversation.Id, conversation);
        DemoJob job = new(Guid.NewGuid(), workspaceId, conversation.Id, request.SessionType.Trim(), request.ClientName.Trim(), "New", DateOnly.FromDateTime(request.StartsAt.UtcDateTime), DateTimeOffset.UtcNow, null, []);
        persistence.Upsert(JobsCollection, job.Id, job);
        DemoConversation updated = conversation with { JobId = job.Id };
        persistence.Upsert(ConversationsCollection, updated.Id, updated);
        return job;
    }

    public DemoJob UpdateJob(Guid id, UpdateJobRequest request)
    {
        DemoJob current = GetJob(id) ?? throw new KeyNotFoundException("Job was not found.");
        DemoJob updated = current with
        {
            Status = string.IsNullOrWhiteSpace(request.Status) ? current.Status : request.Status.Trim(),
            AssignedTo = string.IsNullOrWhiteSpace(request.AssignedTo) ? current.AssignedTo : request.AssignedTo.Trim(),
            EventDate = request.EventDate ?? current.EventDate
        };
        persistence.Upsert(JobsCollection, updated.Id, updated);
        return updated;
    }

    public DemoJob AddFinancialEvent(Guid id, AddFinancialEventRequest request)
    {
        DemoJob current = GetJob(id) ?? throw new KeyNotFoundException("Job was not found.");
        List<DemoFinancialEvent> events = current.FinancialEvents?.ToList() ?? [];
        events.Add(new DemoFinancialEvent(Guid.NewGuid(), request.Kind.Trim(), request.Amount, request.Note?.Trim(), DateTimeOffset.UtcNow));
        DemoJob updated = current with { FinancialEvents = events };
        persistence.Upsert(JobsCollection, updated.Id, updated);
        return updated;
    }

    public AccountsSummary GetAccountsSummary()
    {
        IReadOnlyList<DemoJob> jobs = GetJobs();
        return new(jobs.Sum(x => x.FinancialEvents?.Sum(e => e.Amount) ?? 0m), jobs.Count(x => (x.FinancialEvents?.Count ?? 0) > 0), jobs.Count);
    }

    private static DemoConversation? FindConversationForEmail(InboundEmail email, Guid workspaceId, IReadOnlyList<DemoConversation> conversations)
    {
        string? threadId = NormaliseThreadId(email.ThreadId);
        if (threadId is not null)
        {
            DemoConversation? threaded = conversations.FirstOrDefault(x => x.WorkspaceId == workspaceId && string.Equals(x.ExternalThreadId, threadId, StringComparison.OrdinalIgnoreCase));
            if (threaded is not null)
            {
                return threaded;
            }
        }
        string subject = NormaliseSubject(email.Subject);
        return conversations.Where(x => x.WorkspaceId == workspaceId && string.Equals(x.ContactAddress, email.FromAddress, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(x => string.Equals(NormaliseSubject(x.Subject), subject, StringComparison.OrdinalIgnoreCase));
    }

    private void SeedIfEmpty()
    {
        if (persistence.GetAll<DemoConversation>(ConversationsCollection).Count > 0)
        {
            return;
        }
        DemoConversation wedding = new(Guid.Parse("22222222-2222-2222-2222-222222222222"), WeddingsWorkspaceId, "Johnson Wedding - October 18", "Marsha Johnson", "marsha.johnson@example.com", "Email", false, null,
            [new(Guid.NewGuid(), "Marsha Johnson", "External", "Hi Bryan's, I would like pricing and availability for our wedding on October 18.", DateTimeOffset.UtcNow.AddHours(-4))]);
        persistence.Upsert(ConversationsCollection, wedding.Id, wedding);
    }

    private static DateTimeOffset GetLastActivity(DemoConversation c) => c.Messages.Count == 0 ? DateTimeOffset.MinValue : c.Messages.Max(x => x.CreatedAt);
    private static Guid ResolveWorkspace(Guid? id, string? destination) { if (id.HasValue) return id.Value; string value = destination?.Trim().ToLowerInvariant() ?? string.Empty; if (value.Contains("wedding")) return WeddingsWorkspaceId; if (value.Contains("id") || value.Contains("school")) return IdPhotosWorkspaceId; return GraduationsWorkspaceId; }
    private static string NormaliseChannel(string? channel) => string.IsNullOrWhiteSpace(channel) ? "Manual" : channel.Trim();
    private static string? NormaliseThreadId(string? id) => string.IsNullOrWhiteSpace(id) ? null : id.Trim();
    private static string NormaliseSubject(string subject) { string value = subject.Trim(); while (value.StartsWith("RE:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("FW:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("FWD:", StringComparison.OrdinalIgnoreCase)) { value = value[(value.IndexOf(':') + 1)..].Trim(); } return value; }
}

public sealed record DemoWorkspace(Guid Id, string Name, string Address, int OpenCount);
public sealed record DemoConversation(Guid Id, Guid WorkspaceId, string Subject, string ContactName, string ContactAddress, string Channel, bool IsClosed, Guid? JobId, List<DemoMessage> Messages, string? ExternalThreadId = null);
public sealed record DemoMessage(Guid Id, string Author, string Visibility, string Body, DateTimeOffset CreatedAt, string? ExternalMessageId = null);
public sealed record DemoJob(Guid Id, Guid WorkspaceId, Guid ConversationId, string Title, string ClientName, string Status, DateOnly? EventDate, DateTimeOffset CreatedAt, string? AssignedTo = null, List<DemoFinancialEvent>? FinancialEvents = null);
public sealed record DemoFinancialEvent(Guid Id, string Kind, decimal Amount, string? Note, DateTimeOffset CreatedAt);
public sealed record EmailIngestResult(DemoConversation Conversation, bool CreatedConversation, bool WasDuplicate);
public sealed record IncomingInteractionRequest(Guid? WorkspaceId, string? Destination, string ContactName, string ContactAddress, string Subject, string Body, string? Channel);
public sealed record AddMessageRequest(string Body, bool IsInternal);
public sealed record CreateJobRequest(string Title, DateOnly? EventDate);
public sealed record UpdateJobRequest(string? Status, string? AssignedTo, DateOnly? EventDate);
public sealed record AddFinancialEventRequest(string Kind, decimal Amount, string? Note);
public sealed record AccountsSummary(decimal RecordedAmount, int JobsWithFinancialActivity, int TotalJobs);
