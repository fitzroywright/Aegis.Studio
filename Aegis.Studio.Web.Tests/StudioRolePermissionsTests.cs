using Aegis.Studio.Web.Operations;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class StudioRolePermissionsTests
{
    [Theory]
    [InlineData("{\"adminWorkspace\":true}")]
    [InlineData("{\"AdminWorkspace\":true}")]
    public void WorkspaceAdministratorHasFullPermissions(string json)
        => Assert.Equal(WorkspacePermissionSet.Full, StudioRolePermissions.ReadWorkspace(json));

    [Fact]
    public void OrdinaryWorkspaceRolePreservesLimitedAccess()
    {
        var permissions = StudioRolePermissions.ReadWorkspace("{\"accessWorkspace\":true,\"view\":true}");
        Assert.True(permissions.AccessWorkspace);
        Assert.True(permissions.View);
        Assert.False(permissions.AdminWorkspace);
        Assert.False(permissions.ManageMembers);
    }

    [Fact]
    public void JobAdministratorHasFullPermissions()
        => Assert.Equal(JobPermissionSet.Full, StudioRolePermissions.ReadJob("{\"adminJob\":true}"));

    [Fact]
    public void EmptyRoleDoesNotGrantAccess()
    {
        Assert.Equal(WorkspacePermissionSet.None, StudioRolePermissions.ReadWorkspace("{}"));
        Assert.Equal(JobPermissionSet.None, StudioRolePermissions.ReadJob("{}"));
    }
}
