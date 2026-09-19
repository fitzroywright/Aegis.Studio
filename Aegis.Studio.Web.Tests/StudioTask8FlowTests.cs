using Aegis.Studio.Web;
using Aegis.Studio.Infrastructure.Email;
using Aegis.Studio.Web.Email;
using Aegis.Studio.Web.Storage;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class StudioTask8FlowTests
{
    [Fact]
    public void Inbound_email_flow_has_required_stages()
    {
        Assert.Equal(
            ["Mailbox", "Router", "Workspace", "Conversation", "Notification"],
            StudioFlowSemantics.StageNames("InboundMessage"));
    }

    [Fact]
    public void Outbound_workspace_identity_is_the_safe_default()
    {
        StudioSendingIdentity identity = StudioSendingIdentityResolver.Resolve(
            requestedIdentity: null,
            workspaceEmail: "workspace@bryans.example",
            userName: "manager",
            userEmail: "manager@bryans.example",
            canSendAsSelf: true,
            allowedPersonalSenderDomains: []);

        Assert.Equal(StudioSendingIdentityType.Workspace, identity.Type);
        Assert.Equal("workspace@bryans.example", identity.FromAddress);
    }

    [Fact]
    public void Graph_send_endpoint_uses_selected_sending_identity()
    {
        Assert.Equal(
            "https://graph.microsoft.com/v1.0/users/manager%40bryans.example/sendMail",
            MicrosoftGraphEmailClient.BuildSendMailUrl("manager@bryans.example"));
    }

    [Fact]
    public void Authorized_reply_as_myself_uses_profile_email_on_workspace_domain()
    {
        StudioSendingIdentity identity = StudioSendingIdentityResolver.Resolve(
            requestedIdentity: "Self",
            workspaceEmail: "workspace@bryans.example",
            userName: "manager",
            userEmail: "manager@bryans.example",
            canSendAsSelf: true,
            allowedPersonalSenderDomains: []);

        Assert.Equal(StudioSendingIdentityType.Self, identity.Type);
        Assert.Equal("manager@bryans.example", identity.FromAddress);
        Assert.Equal("manager", identity.UserName);
    }

    [Fact]
    public void Unauthorized_reply_as_myself_is_denied()
    {
        Assert.Throws<UnauthorizedAccessException>(() =>
            StudioSendingIdentityResolver.Resolve(
                requestedIdentity: "Self",
                workspaceEmail: "workspace@bryans.example",
                userName: "staff",
                userEmail: "staff@bryans.example",
                canSendAsSelf: false,
                allowedPersonalSenderDomains: []));
    }

    [Fact]
    public void Attachment_storage_failure_is_localized_to_storage_stage()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IReadOnlyList<StudioFlowStageSnapshot> stages =
            StudioFlowSemantics.Linear("AttachmentProcessing", "Storage", now, true, "StorageUnavailable");

        Assert.Equal("Completed", stages.Single(x => x.Name == "Received").State);
        Assert.Equal("Failed", stages.Single(x => x.Name == "Storage").State);
        Assert.Equal("Pending", stages.Single(x => x.Name == "Metadata").State);
        Assert.Equal("Pending", stages.Single(x => x.Name == "Workspace").State);
    }

    [Fact]
    public void Gallery_publish_failure_does_not_claim_publication_or_verification()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IReadOnlyList<StudioFlowStageSnapshot> stages =
            StudioFlowSemantics.Linear("GalleryPublish", "Publish", now, true, "PublishFailed");

        Assert.Equal("Completed", stages.Single(x => x.Name == "InternalStorage").State);
        Assert.Equal("Failed", stages.Single(x => x.Name == "Publish").State);
        Assert.Equal("Pending", stages.Single(x => x.Name == "PublicGallery").State);
        Assert.Equal("Pending", stages.Single(x => x.Name == "Verification").State);
    }

    [Fact]
    public void One_notification_provider_failure_does_not_overwrite_successful_branch()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var tracker = new StudioNotificationBranchTracker();

        tracker.Observe("corr-1", "Slack", failed: false, now);
        tracker.Observe("corr-1", "Email", failed: true, now.AddSeconds(1), "DeadLetter");

        IReadOnlyDictionary<string, StudioNotificationBranchObservation> state = tracker.Get("corr-1");

        Assert.Equal("Completed", state["Slack"].State);
        Assert.Equal("Failed", state["Email"].State);
    }

    [Fact]
    public void Branching_status_keeps_unobserved_branches_pending()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var observed = new Dictionary<string, (string State, DateTimeOffset AtUtc, string? Error)>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["InApp"] = ("Completed", now, null),
            ["Email"] = ("Failed", now.AddSeconds(1), "DeadLetter")
        };

        IReadOnlyList<StudioFlowStageSnapshot> stages =
            StudioFlowSemantics.NotificationFanout(observed, now);

        Assert.Equal("Completed", stages.Single(x => x.Name == "Event").State);
        Assert.Equal("Completed", stages.Single(x => x.Name == "InApp").State);
        Assert.Equal("Failed", stages.Single(x => x.Name == "Email").State);
        Assert.Equal("Pending", stages.Single(x => x.Name == "Slack").State);
        Assert.Equal("Pending", stages.Single(x => x.Name == "Teams").State);
    }

    [Fact]
    public void Provider_acceptance_without_delivery_receipt_leaves_recipient_unknown()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IReadOnlyList<StudioFlowStageSnapshot> stages =
            StudioFlowSemantics.Linear(
                "OutboundMessage",
                "Recipient",
                now,
                failed: false,
                detail: "DeliveryReceiptUnavailable",
                stateOverride: "Unknown");

        Assert.Equal("Unknown", stages.Single(x => x.Name == "Recipient").State);
        Assert.Null(stages.Single(x => x.Name == "Recipient").CompletedAtUtc);
        Assert.Equal("Completed", stages.Single(x => x.Name == "Provider").State);
    }

    [Fact]
    public void Storage_metadata_preserves_application_conversation_and_correlation()
    {
        Guid conversationId = Guid.NewGuid();
        IReadOnlyDictionary<string,string> metadata =
            AttachmentStore.BuildStorageMetadata(conversationId, "External", "corr-storage-1");

        Assert.Equal("Aegis.Studio", metadata["Application"]);
        Assert.Equal(conversationId.ToString("D"), metadata["ConversationId"]);
        Assert.Equal("corr-storage-1", metadata["CorrelationId"]);
    }

    [Fact]
    public void Correlation_continuity_prefers_business_reference_over_durable_queue_id()
    {
        Guid durableId = Guid.NewGuid();

        Assert.Equal(
            "conversation-123",
            StudioFlowSemantics.CorrelationId("conversation-123", durableId));

        Assert.Equal(
            durableId.ToString("D"),
            StudioFlowSemantics.CorrelationId(null, durableId));
    }
}
