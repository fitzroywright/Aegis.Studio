using System.Text.Json;
using Aegis.Studio.Web.Operations;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class AuthorizationTests
{
    [Theory]
    [InlineData("/api/auth/login")]
    [InlineData("/api/bookings")]
    [InlineData("/api/bookings/{id:guid}")]
    [InlineData("/api/bookings/{id:guid}/payment-confirmations")]
    public void ApprovedAnonymousRoutesAreExplicit(string route)
    {
        Assert.True(AuthorizationAuditPolicy.IsApprovedAnonymousPath(route));
    }

    [Theory]
    [InlineData("/api/workspaces")]
    [InlineData("/api/jobs")]
    [InlineData("/api/security/roles")]
    [InlineData("/api/workflows")]
    [InlineData("/api/schedule/entries")]
    public void InternalRoutesAreNotApprovedAnonymous(string route)
    {
        Assert.False(AuthorizationAuditPolicy.IsApprovedAnonymousPath(route));
    }

    [Fact]
    public void WorkspaceAdminPresetContainsEveryWorkspacePermission()
    {
        WorkspacePermissionSet permissions = WorkspacePermissionSet.Full;
        Assert.True(permissions.AccessWorkspace && permissions.View && permissions.ReplyRequester && permissions.ViewAttachments && permissions.AddAttachments);
        Assert.True(permissions.CreateJob && permissions.EditJob && permissions.ManageSchedule && permissions.ManageProduction && permissions.NotifyMembers);
        Assert.True(permissions.EditWorkspace && permissions.ManageMembers && permissions.AdminWorkspace);
    }

    [Fact]
    public void WorkspaceMemberManagerCannotGrantWorkspaceAdminBeyondOwnRights()
    {
        WorkspacePermissionSet caller = new(true,true,true,true,true,true,true,true,true,true,false,true,false);
        Assert.False(WorkspaceAccessEndpoints.PermissionsWithin(WorkspacePermissionSet.Full, caller));
        Assert.True(WorkspaceAccessEndpoints.PermissionsWithin(WorkspacePermissionSet.ViewOnly, caller));
    }

    [Fact]
    public void JobManagerDoesNotImplicitlyGainRefundVoidPermission()
    {
        Assert.False(JobPermissionSet.JobManager.RefundVoidPos);
        Assert.True(JobPermissionSet.Full.RefundVoidPos);
    }

    [Fact]
    public void JobManagerCannotGrantRefundVoidOrAdminBeyondOwnRights()
    {
        Assert.False(JobAccessEndpoints.PermissionsWithin(JobPermissionSet.Full, JobPermissionSet.JobManager));
        Assert.True(JobAccessEndpoints.PermissionsWithin(JobPermissionSet.Viewer, JobPermissionSet.JobManager));
    }

    [Fact]
    public void ReusableRolePermissionJsonRoundTrips()
    {
        string json = JsonSerializer.Serialize(WorkspacePermissionSet.ViewOnly);
        WorkspacePermissionSet? value = JsonSerializer.Deserialize<WorkspacePermissionSet>(json);
        Assert.NotNull(value);
        Assert.True(value!.AccessWorkspace);
        Assert.True(value.View);
        Assert.False(value.EditWorkspace);
    }
}
