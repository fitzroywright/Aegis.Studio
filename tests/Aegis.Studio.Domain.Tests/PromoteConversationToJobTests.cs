using Aegis.Studio.Application.Conversations;
using Aegis.Studio.Domain.Conversations;
using Aegis.Studio.Domain.Jobs;

using Xunit;

namespace Aegis.Studio.Domain.Tests;

public sealed class PromoteConversationToJobTests
{
    [Fact]
    public void ExecuteLinksConversationAndJobWithoutReplacingConversation()
    {
        Guid workspaceId = Guid.NewGuid();
        Guid contactId = Guid.NewGuid();
        Conversation conversation = new(Guid.NewGuid(), workspaceId, contactId, "Wedding inquiry", DateTimeOffset.UtcNow);
        PromoteConversationToJob handler = new();

        Job job = handler.Execute(conversation, "Wright Wedding", DateTimeOffset.UtcNow);

        Assert.Equal(job.Id, conversation.JobId);
        Assert.Equal(workspaceId, job.WorkspaceId);
        Assert.Equal(contactId, job.ContactId);
        Assert.Equal(JobStatus.New, job.Status);
    }
}
