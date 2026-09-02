using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Domain.Tasks;

namespace Aegis.Studio.Application.Tasks;

public sealed class TaskService
{
    private readonly ITaskRepository taskRepository;
    private readonly ITaskSyncProvider taskSyncProvider;
    private readonly IUnitOfWork unitOfWork;

    public TaskService(ITaskRepository taskRepository, ITaskSyncProvider taskSyncProvider, IUnitOfWork unitOfWork)
    {
        this.taskRepository = taskRepository;
        this.taskSyncProvider = taskSyncProvider;
        this.unitOfWork = unitOfWork;
    }

    public async Task<StudioTask> CreateAsync(Guid workspaceId, Guid? bookingId, Guid? conversationId, string title, string description, string? assignedUserId, DateTimeOffset? dueUtc, CancellationToken cancellationToken)
    {
        StudioTask task = new(workspaceId, bookingId, conversationId, title, description, assignedUserId, dueUtc);
        await taskRepository.AddAsync(task, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await taskSyncProvider.UpsertAsync(task, cancellationToken);
        return task;
    }

    public async Task CompleteAsync(Guid taskId, CancellationToken cancellationToken)
    {
        StudioTask task = await taskRepository.GetAsync(taskId, cancellationToken) ?? throw new InvalidOperationException("Task was not found.");
        task.Complete();
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await taskSyncProvider.UpsertAsync(task, cancellationToken);
    }
}
