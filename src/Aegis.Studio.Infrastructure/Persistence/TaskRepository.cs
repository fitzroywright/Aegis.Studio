using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Domain.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Studio.Infrastructure.Persistence;

public sealed class TaskRepository : ITaskRepository
{
    private readonly StudioDbContext dbContext;

    public TaskRepository(StudioDbContext dbContext)
    {
        this.dbContext = dbContext;
    }

    public async Task<StudioTask?> GetAsync(Guid taskId, CancellationToken cancellationToken) => await dbContext.StudioTasks.SingleOrDefaultAsync(item => item.Id == taskId, cancellationToken);
    public async Task<IReadOnlyList<StudioTask>> GetForWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken) => await dbContext.StudioTasks.AsNoTracking().Where(item => item.WorkspaceId == workspaceId).OrderBy(item => item.Status).ThenBy(item => item.DueUtc).ToListAsync(cancellationToken);
    public async Task AddAsync(StudioTask task, CancellationToken cancellationToken) => await dbContext.StudioTasks.AddAsync(task, cancellationToken);
}
