using Aegis.Studio.Domain.Common;

namespace Aegis.Studio.Domain.Conversations;

public sealed class Conversation : Entity
{
    private readonly List<ConversationMessage> messages = new();

    public Conversation(Guid id, Guid workspaceId, Guid contactId, string subject, DateTimeOffset openedAt) : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        WorkspaceId = workspaceId;
        ContactId = contactId;
        Subject = subject.Trim();
        OpenedAt = openedAt;
    }

    public Guid WorkspaceId { get; }

    public Guid ContactId { get; }

    public string Subject { get; }

    public DateTimeOffset OpenedAt { get; }

    public Guid? JobId { get; private set; }

    public IReadOnlyCollection<ConversationMessage> Messages => messages.AsReadOnly();

    public void AddMessage(ConversationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.ConversationId != Id)
        {
            throw new InvalidOperationException("The message belongs to a different conversation.");
        }

        messages.Add(message);
    }

    public void LinkToJob(Guid jobId)
    {
        if (JobId.HasValue && JobId.Value != jobId)
        {
            throw new InvalidOperationException("A conversation cannot be silently moved between jobs.");
        }

        JobId = jobId;
    }
}
