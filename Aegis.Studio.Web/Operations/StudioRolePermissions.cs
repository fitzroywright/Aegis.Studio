using System.Text.Json;

namespace Aegis.Studio.Web.Operations;

public static class StudioRolePermissions
{
    public static WorkspacePermissionSet ReadWorkspace(string json)
    {
        WorkspacePermissionSet permissions = JsonSerializer.Deserialize<WorkspacePermissionSet>(json, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException("Invalid Workspace permission set.");
        return permissions.AdminWorkspace ? WorkspacePermissionSet.Full : permissions;
    }

    public static JobPermissionSet ReadJob(string json)
    {
        JobPermissionSet permissions = JsonSerializer.Deserialize<JobPermissionSet>(json, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException("Invalid Job permission set.");
        return permissions.AdminJob ? JobPermissionSet.Full : permissions;
    }
}
