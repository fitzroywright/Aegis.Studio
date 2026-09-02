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

    public async Task StartAsync(Guid taskId, CancellationToken cancellationToken)
    {
        StudioTask task = await GetTaskAsync(taskId, cancellationToken);
        task.Start();
        await SaveAndSyncAsync(task, cancellationToken);
    }

    public async Task AssignAsync(Guid taskId, string? userId, CancellationToken cancellationToken)
    {
        StudioTask task = await GetTaskAsync(taskId, cancellationToken);
        task.AssignTo(userId);
        await SaveAndSyncAsync(task, cancellationToken);
    }

    public async Task CompleteAsync(Guid taskId, CancellationToken cancellationToken)
    {
        StudioTask task = await GetTaskAsync(taskId, cancellationToken);
        task.Complete();
        await SaveAndSyncAsync(task, cancellationToken);
    }

    private async Task<StudioTask> GetTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        return await taskRepository.GetAsync(taskId, cancellationToken) ?? throw new InvalidOperationException("Task was not found.");
    }

    private async Task SaveAndSyncAsync(StudioTask task, CancellationToken cancellationToken)
    {
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await taskSyncProvider.UpsertAsync(task, cancellationToken);
    }
}
