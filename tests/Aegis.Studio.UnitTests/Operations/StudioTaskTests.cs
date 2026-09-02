using Aegis.Studio.Domain.Tasks;

namespace Aegis.Studio.UnitTests.Operations;

public sealed class StudioTaskTests
{
    [Fact]
    public void TaskMovesThroughOperationalWorkflow()
    {
        StudioTask task = new(Guid.NewGuid(), null, null, "Call customer", "Confirm session details", null, DateTimeOffset.UtcNow.AddHours(2));
        Assert.Equal(StudioTaskStatus.Open, task.Status);
        task.AssignTo("ryan");
        task.Start();
        Assert.Equal("ryan", task.AssignedUserId);
        Assert.Equal(StudioTaskStatus.InProgress, task.Status);
        task.Complete();
        Assert.Equal(StudioTaskStatus.Completed, task.Status);
        Assert.NotNull(task.CompletedUtc);
    }
}
