using Aegis.Studio.Domain.Workspaces;

namespace Aegis.Studio.Application.Abstractions;

public interface IWorkspaceRepository
{
    Task<IReadOnlyList<WorkspaceMember>> GetMembersAsync(Guid workspaceId, CancellationToken cancellationToken);
    Task<WorkspaceMember?> GetMembershipAsync(Guid workspaceId, string userId, CancellationToken cancellationToken);
    Task AddMemberAsync(WorkspaceMember member, CancellationToken cancellationToken);
}
