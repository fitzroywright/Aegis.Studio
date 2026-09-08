using Aegis.Studio.Domain.Conversations;
using Aegis.Studio.Domain.Jobs;

namespace Aegis.Studio.Application.Conversations;

public sealed class PromoteConversationToJob
{
    public Job Execute(Conversation conversation, string jobTitle, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobTitle);

        Job job = new(Guid.NewGuid(), conversation.WorkspaceId, conversation.ContactId, jobTitle, createdAt);
        conversation.LinkToJob(job.Id);

        return job;
    }
}
