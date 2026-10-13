using Aegis.Studio.Web.Operations;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class DemoMaintenanceNativeTests
{
    [StudioPostgresFact,Trait("Database","Native")]
    public void CompetingResetIsRejectedWithoutWaiting()
    {
        using var context=new IntegratedWorkflowTests.WorkflowContext();
        using var reset=DemoMaintenance.ReserveReset(context.Database);
        Assert.Throws<InvalidOperationException>(()=>DemoMaintenance.ReserveReset(context.Database));
    }

    [StudioPostgresFact,Trait("Database","Native")]
    public async Task IndependentRequestsAreBlockedDuringResetAndResumeWhenReleased()
    {
        using var context=new IntegratedWorkflowTests.WorkflowContext();
        using(var reset=DemoMaintenance.EnterReset(context.Database))
        {
            Task<bool> probe;
            using(ExecutionContext.SuppressFlow())probe=Task.Run(()=>
            {
                using var operation=DemoMaintenance.TryEnterOperation(context.Database);
                return operation is null;
            });
            Assert.True(await probe);
        }
        using var resumed=DemoMaintenance.TryEnterOperation(context.Database);
        Assert.NotNull(resumed);
    }
}
