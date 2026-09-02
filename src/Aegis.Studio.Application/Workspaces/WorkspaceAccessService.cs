using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Domain.Workspaces;

namespace Aegis.Studio.Application.Workspaces;

public sealed class WorkspaceAccessService
{
    private readonly IWorkspaceRepository workspaceRepository;

    public WorkspaceAccessService(IWorkspaceRepository workspaceRepository)
    {
        this.workspaceRepository = workspaceRepository;
    }

    public async Task<bool> CanAccessAsync(Guid workspaceId, string userId, CancellationToken cancellationToken)
    {
        WorkspaceMember? membership = await workspaceRepository.GetMembershipAsync(workspaceId, userId, cancellationToken);
        return membership is not null && membership.IsActive;
    }

    public async Task<bool> CanManageAsync(Guid workspaceId, string userId, CancellationToken cancellationToken)
    {
        WorkspaceMember? membership = await workspaceRepository.GetMembershipAsync(workspaceId, userId, cancellationToken);
        return membership is not null && membership.IsActive && membership.Role >= WorkspaceRole.Lead;
    }
}
