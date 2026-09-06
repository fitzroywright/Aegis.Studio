using Aegis.Studio.Application.EmailRouting;
using Aegis.Studio.Web.Bookings;
using Aegis.Studio.Web.Persistence;

namespace Aegis.Studio.Web.Operations;

public sealed class StudioStore
{
    private const string ConversationsCollection = "conversations";
    private const string JobsCollection = "jobs";
    private static readonly Guid WeddingsWorkspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid IdPhotosWorkspaceId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid GraduationsWorkspaceId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private readonly PostgresDocumentStore persistence;

    public StudioStore(PostgresDocumentStore persistence)
    {
        this.persistence = persistence;
    }

    public IReadOnlyList<StudioWorkspace> GetWorkspaces()
    {
        IReadOnlyList<StudioConversation> conversations = persistence.GetAll<StudioConversation>(ConversationsCollection);
        return
        [
            new(WeddingsWorkspaceId, "Weddings", "weddings@bryanstudiosltd.com", conversations.Count(x => x.WorkspaceId == WeddingsWorkspaceId && !x.IsClosed)),
            new(IdPhotosWorkspaceId, "ID Photos", "idphotos@bryanstudiosltd.com", conversations.Count(x => x.WorkspaceId == IdPhotosWorkspaceId && !x.IsClosed)),
            new(GraduationsWorkspaceId, "Graduations", "graduations@bryanstudiosltd.com", conversations.Count(x => x.WorkspaceId == GraduationsWorkspaceId && !x.IsClosed))
        ];
    }

    public IReadOnlyList<StudioConversation> GetConversations(Guid? workspaceId)
    {
        IEnumerable<StudioConversation> query = persistence.GetAll<StudioConversation>(ConversationsCollection).Where(x => !x.IsClosed);
        if (workspaceId.HasValue)
        {
            query = query.Where(x => x.WorkspaceId == workspaceId.Value);
        }
        return query.OrderByDescending(GetLastActivity).ToList();
    }

    public StudioConversation? GetConversation(Guid id) => persistence.Get<StudioConversation>(ConversationsCollection, id);
    public StudioJob? GetJob(Guid id) => persistence.Get<StudioJob>(JobsCollection, id);
    public IReadOnlyList<StudioJob> GetJobs() => persistence.GetAll<StudioJob>(JobsCollection).OrderByDescending(x => x.CreatedAt).ToList();

    public StudioConversation ReceiveInteraction(IncomingInteractionRequest request)
    {
        Guid workspaceId = ResolveWorkspace(request.WorkspaceId, request.Destination);
        StudioConversation conversation = new(Guid.NewGuid(), workspaceId, request.Subject.Trim(), request.ContactName.Trim(), request.ContactAddress.Trim(), NormaliseChannel(request.Channel), false, null,
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

        List<StudioConversation> conversations = persistence.GetAll<StudioConversation>(ConversationsCollection).ToList();
        StudioConversation? duplicate = conversations.FirstOrDefault(x => x.Messages.Any(m => string.Equals(m.ExternalMessageId, email.MessageId, StringComparison.OrdinalIgnoreCase)));
        if (duplicate is not null)
        {
            return new EmailIngestResult(duplicate, false, true);
        }

        StudioConversation? existing = FindConversationForEmail(email, route.WorkspaceId.Value, conversations);
        StudioMessage message = new(Guid.NewGuid(), email.FromName.Trim(), "External", email.Body.Trim(), email.ReceivedAt.ToUniversalTime(), email.MessageId);
        if (existing is not null)
        {
            existing.Messages.Add(message);
            persistence.Upsert(ConversationsCollection, existing.Id, existing);
            return new EmailIngestResult(existing, false, false);
        }

        StudioConversation conversation = new(Guid.NewGuid(), route.WorkspaceId.Value, email.Subject.Trim(), email.FromName.Trim(), email.FromAddress.Trim(), "Email", false, null, [message], NormaliseThreadId(email.ThreadId));
        persistence.Upsert(ConversationsCollection, conversation.Id, conversation);
        return new EmailIngestResult(conversation, true, false);
    }

    public StudioConversation AddMessage(Guid id, AddMessageRequest request)
    {
        StudioConversation conversation = GetConversation(id) ?? throw new KeyNotFoundException("Conversation was not found.");
        conversation.Messages.Add(new StudioMessage(Guid.NewGuid(), request.IsInternal ? "Aegis Staff" : "Bryan's Studio", request.IsInternal ? "Internal" : "External", request.Body.Trim(), DateTimeOffset.UtcNow));
        persistence.Upsert(ConversationsCollection, conversation.Id, conversation);
        return conversation;
    }

    public StudioJob CreateJob(Guid conversationId, CreateJobRequest request)
    {
        StudioConversation conversation = GetConversation(conversationId) ?? throw new KeyNotFoundException("Conversation was not found.");
        if (conversation.JobId.HasValue)
        {
            StudioJob? existing = GetJob(conversation.JobId.Value);
            if (existing is not null)
            {
                return existing;
            }
        }

        StudioJob job = new(Guid.NewGuid(), conversation.WorkspaceId, conversation.Id, request.Title.Trim(), conversation.ContactName, "New", request.EventDate, DateTimeOffset.UtcNow, null, []);
        persistence.Upsert(JobsCollection, job.Id, job);
        StudioConversation updated = conversation with { JobId = job.Id };
        persistence.Upsert(ConversationsCollection, updated.Id, updated);
        return job;
    }

    public StudioJob CreateBookingJob(CreateBookingIntakeRequest request)
    {
        Guid workspaceId = ResolveWorkspace(request.WorkspaceId, request.Destination ?? request.SessionType);
        string channel = NormaliseChannel(request.Source);
        string address = !string.IsNullOrWhiteSpace(request.ClientEmail) ? request.ClientEmail.Trim() : request.ClientPhone?.Trim() ?? "Walk-in";
        string note = string.IsNullOrWhiteSpace(request.Notes) ? $"Booking request for {request.SessionType}." : request.Notes.Trim();
        StudioConversation conversation = new(Guid.NewGuid(), workspaceId, $"{request.SessionType} booking", request.ClientName.Trim(), address, channel, false, null,
            [new(Guid.NewGuid(), request.ClientName.Trim(), "External", note, DateTimeOffset.UtcNow)]);
        persistence.Upsert(ConversationsCollection, conversation.Id, conversation);
        StudioJob job = new(Guid.NewGuid(), workspaceId, conversation.Id, request.SessionType.Trim(), request.ClientName.Trim(), "New", DateOnly.FromDateTime(request.StartsAt.UtcDateTime), DateTimeOffset.UtcNow, null, []);
        persistence.Upsert(JobsCollection, job.Id, job);
        StudioConversation updated = conversation with { JobId = job.Id };
        persistence.Upsert(ConversationsCollection, updated.Id, updated);
        return job;
    }

    public StudioJob UpdateJob(Guid id, UpdateJobRequest request)
    {
        StudioJob current = GetJob(id) ?? throw new KeyNotFoundException("Job was not found.");
        StudioJob updated = current with
        {
            Status = string.IsNullOrWhiteSpace(request.Status) ? current.Status : request.Status.Trim(),
            AssignedTo = string.IsNullOrWhiteSpace(request.AssignedTo) ? current.AssignedTo : request.AssignedTo.Trim(),
            EventDate = request.EventDate ?? current.EventDate
        };
        persistence.Upsert(JobsCollection, updated.Id, updated);
        return updated;
    }

    public StudioJob AddFinancialEvent(Guid id, AddFinancialEventRequest request)
    {
        StudioJob current = GetJob(id) ?? throw new KeyNotFoundException("Job was not found.");
        List<StudioFinancialEvent> events = current.FinancialEvents?.ToList() ?? [];
        events.Add(new StudioFinancialEvent(Guid.NewGuid(), request.Kind.Trim(), request.Amount, request.Note?.Trim(), DateTimeOffset.UtcNow));
        StudioJob updated = current with { FinancialEvents = events };
        persistence.Upsert(JobsCollection, updated.Id, updated);
        return updated;
    }

    public AccountsSummary GetAccountsSummary()
    {
        IReadOnlyList<StudioJob> jobs = GetJobs();
        return new(jobs.Sum(x => x.FinancialEvents?.Sum(e => e.Amount) ?? 0m), jobs.Count(x => (x.FinancialEvents?.Count ?? 0) > 0), jobs.Count);
    }

    private static StudioConversation? FindConversationForEmail(InboundEmail email, Guid workspaceId, IReadOnlyList<StudioConversation> conversations)
    {
        string? threadId = NormaliseThreadId(email.ThreadId);
        if (threadId is not null)
        {
            StudioConversation? threaded = conversations.FirstOrDefault(x => x.WorkspaceId == workspaceId && string.Equals(x.ExternalThreadId, threadId, StringComparison.OrdinalIgnoreCase));
            if (threaded is not null)
            {
                return threaded;
            }
        }
        string subject = NormaliseSubject(email.Subject);
        return conversations.Where(x => x.WorkspaceId == workspaceId && string.Equals(x.ContactAddress, email.FromAddress, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(x => string.Equals(NormaliseSubject(x.Subject), subject, StringComparison.OrdinalIgnoreCase));
    }

    private static DateTimeOffset GetLastActivity(StudioConversation conversation) => conversation.Messages.Count == 0 ? DateTimeOffset.MinValue : conversation.Messages.Max(x => x.CreatedAt);
    private static Guid ResolveWorkspace(Guid? id, string? destination) { if (id.HasValue) return id.Value; string value = destination?.Trim().ToLowerInvariant() ?? string.Empty; if (value.Contains("wedding")) return WeddingsWorkspaceId; if (value.Contains("id") || value.Contains("school")) return IdPhotosWorkspaceId; return GraduationsWorkspaceId; }
    private static string NormaliseChannel(string? channel) => string.IsNullOrWhiteSpace(channel) ? "Manual" : channel.Trim();
    private static string? NormaliseThreadId(string? id) => string.IsNullOrWhiteSpace(id) ? null : id.Trim();
    private static string NormaliseSubject(string subject) { string value = subject.Trim(); while (value.StartsWith("RE:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("FW:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("FWD:", StringComparison.OrdinalIgnoreCase)) { value = value[(value.IndexOf(':') + 1)..].Trim(); } return value; }
}

public sealed record StudioWorkspace(Guid Id, string Name, string Address, int OpenCount);
public sealed record StudioConversation(Guid Id, Guid WorkspaceId, string Subject, string ContactName, string ContactAddress, string Channel, bool IsClosed, Guid? JobId, List<StudioMessage> Messages, string? ExternalThreadId = null);
public sealed record StudioMessage(Guid Id, string Author, string Visibility, string Body, DateTimeOffset CreatedAt, string? ExternalMessageId = null);
public sealed record StudioJob(Guid Id, Guid WorkspaceId, Guid ConversationId, string Title, string ClientName, string Status, DateOnly? EventDate, DateTimeOffset CreatedAt, string? AssignedTo = null, List<StudioFinancialEvent>? FinancialEvents = null);
public sealed record StudioFinancialEvent(Guid Id, string Kind, decimal Amount, string? Note, DateTimeOffset CreatedAt);
public sealed record EmailIngestResult(StudioConversation Conversation, bool CreatedConversation, bool WasDuplicate);
public sealed record IncomingInteractionRequest(Guid? WorkspaceId, string? Destination, string ContactName, string ContactAddress, string Subject, string Body, string? Channel);
public sealed record AddMessageRequest(string Body, bool IsInternal);
public sealed record CreateJobRequest(string Title, DateOnly? EventDate);
public sealed record UpdateJobRequest(string? Status, string? AssignedTo, DateOnly? EventDate);
public sealed record AddFinancialEventRequest(string Kind, decimal Amount, string? Note);
public sealed record AccountsSummary(decimal RecordedAmount, int JobsWithFinancialActivity, int TotalJobs);
