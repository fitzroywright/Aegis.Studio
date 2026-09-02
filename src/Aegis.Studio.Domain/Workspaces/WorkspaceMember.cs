namespace Aegis.Studio.Domain.Workspaces;

public sealed class WorkspaceMember
{
    private WorkspaceMember()
    {
    }

    public WorkspaceMember(Guid workspaceId, string userId, string displayName, WorkspaceRole role)
    {
        Id = Guid.NewGuid();
        WorkspaceId = workspaceId;
        UserId = userId.Trim();
        DisplayName = displayName.Trim();
        Role = role;
        IsActive = true;
    }

    public Guid Id { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public string UserId { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;
    public WorkspaceRole Role { get; private set; }
    public bool IsActive { get; private set; }
}
