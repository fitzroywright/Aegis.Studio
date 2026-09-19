using Aegis.Studio.Web;
using Aegis.Studio.Web.Email;
using Aegis.Studio.Web.Operations;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class StudioTask8MessagingTests
{
    [Fact]
    public void Workspace_identity_is_the_safe_default()
    {
        StudioSendingIdentity identity = StudioSendingIdentityResolver.Resolve(
            null,
            "workspace@bryanstudio.test",
            "photographer1",
            "photographer1@bryanstudio.test",
            canSendAsSelf: true,
            allowedPersonalSenderDomains: []);

        Assert.Equal(StudioSendingIdentityType.Workspace, identity.Type);
        Assert.Equal("workspace@bryanstudio.test", identity.FromAddress);
    }

    [Fact]
    public void Authorized_user_can_send_as_self_on_workspace_domain()
    {
        StudioSendingIdentity identity = StudioSendingIdentityResolver.Resolve(
            "Self",
            "workspace@bryanstudio.test",
            "manager1",
            "manager1@bryanstudio.test",
            canSendAsSelf: true,
            allowedPersonalSenderDomains: []);

        Assert.Equal(StudioSendingIdentityType.Self, identity.Type);
        Assert.Equal("manager1@bryanstudio.test", identity.FromAddress);
        Assert.Equal("manager1", identity.UserName);
    }

    [Fact]
    public void Explicit_secondary_studio_domain_can_be_allowed()
    {
        StudioSendingIdentity identity = StudioSendingIdentityResolver.Resolve(
            "Self",
            "workspace@bryanstudio.test",
            "manager1",
            "manager1@bryans.example",
            canSendAsSelf: true,
            allowedPersonalSenderDomains: ["bryans.example"]);

        Assert.Equal(StudioSendingIdentityType.Self, identity.Type);
    }

    [Fact]
    public void Unauthorized_user_cannot_send_as_self()
    {
        Assert.Throws<UnauthorizedAccessException>(() =>
            StudioSendingIdentityResolver.Resolve(
                "Self",
                "workspace@bryanstudio.test",
                "user1",
                "user1@bryanstudio.test",
                canSendAsSelf: false,
                allowedPersonalSenderDomains: []));
    }

    [Fact]
    public void Unapproved_external_email_cannot_send_as_self()
    {
        Assert.Throws<InvalidOperationException>(() =>
            StudioSendingIdentityResolver.Resolve(
                "Self",
                "workspace@bryanstudio.test",
                "user1",
                "user1@example.net",
                canSendAsSelf: true,
                allowedPersonalSenderDomains: []));
    }

    [Fact]
    public void Send_as_self_permission_cannot_be_delegated_by_user_without_it()
    {
        WorkspacePermissionSet caller = WorkspacePermissionSet.Full with { SendAsSelf = false };
        WorkspacePermissionSet requested = WorkspacePermissionSet.ViewOnly with { SendAsSelf = true };

        Assert.False(WorkspaceAccessEndpoints.PermissionsWithin(requested, caller));
    }

    [Fact]
    public void Notification_branches_remain_independent()
    {
        var tracker = new StudioNotificationBranchTracker();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        tracker.Observe("corr-1", "Email", failed: true, now, "ProviderUnavailable");
        tracker.Observe("corr-1", "Slack", failed: false, now.AddSeconds(1));
        tracker.Observe("corr-1", "InApp", failed: false, now.AddSeconds(2));

        IReadOnlyDictionary<string, StudioNotificationBranchObservation> state = tracker.Get("corr-1");

        Assert.Equal("Failed", state["Email"].State);
        Assert.Equal("Completed", state["Slack"].State);
        Assert.Equal("Completed", state["InApp"].State);
    }

    [Fact]
    public void Failed_notification_branch_can_recover_without_changing_other_branches()
    {
        var tracker = new StudioNotificationBranchTracker();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        tracker.Observe("corr-2", "Email", failed: true, now, "ProviderUnavailable");
        tracker.Observe("corr-2", "Slack", failed: false, now);
        tracker.Observe("corr-2", "Email", failed: false, now.AddMinutes(1), "ProviderAccepted");

        IReadOnlyDictionary<string, StudioNotificationBranchObservation> state = tracker.Get("corr-2");

        Assert.Equal("Completed", state["Email"].State);
        Assert.Equal("Completed", state["Slack"].State);
    }

    [Fact]
    public void Different_correlations_do_not_share_branch_state()
    {
        var tracker = new StudioNotificationBranchTracker();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        tracker.Observe("corr-a", "Email", failed: true, now);
        tracker.Observe("corr-b", "Email", failed: false, now);

        Assert.Equal("Failed", tracker.Get("corr-a")["Email"].State);
        Assert.Equal("Completed", tracker.Get("corr-b")["Email"].State);
    }
}
