using Aegis.Studio.Domain.Communications;
using Xunit;

namespace Aegis.Studio.UnitTests.Communications;

public sealed class ConversationWorkflowTests
{
    [Fact]
    public void StaffReplyMovesConversationToWaiting()
    {
        Conversation conversation = new(Guid.NewGuid(), null, "client@example.com", "Question");
        conversation.MarkWaitingForCustomer();
        Assert.Equal(ConversationStatus.WaitingForCustomer, conversation.Status);
        Assert.NotNull(conversation.LastStaffResponseUtc);
    }

    [Fact]
    public void CustomerReplyReopensWaitingConversation()
    {
        Conversation conversation = new(Guid.NewGuid(), null, "client@example.com", "Question");
        conversation.MarkWaitingForCustomer();
        conversation.ReceiveInbound();
        Assert.Equal(ConversationStatus.Open, conversation.Status);
        Assert.True(conversation.IsUnread);
    }
}
