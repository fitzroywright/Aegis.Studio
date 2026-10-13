using Aegis.Studio.Web.Workflow;
using Common.Messaging;
using Npgsql;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class DemoDeliveryEpochTests
{
    [StudioPostgresFact]
    public async Task ReusedNotificationIdCannotSendAnOldDatasetPayload()
    {
        using var context=new IntegratedWorkflowTests.WorkflowContext();
        Guid notification=Guid.NewGuid();
        using(var command=new NpgsqlCommand("insert into studio_notifications(id,user_name,channel,event_type,subject,body,status,created_at_utc) values($1,'staff','Email','JobStatusChanged','New event','New payload','Pending',now())",context.Connection))
        {command.Parameters.AddWithValue(notification);command.ExecuteNonQuery();}
        using(var command=new NpgsqlCommand("insert into studio_state(key,value,updated_at_utc) values('studio-dataset-epoch','new-dataset',now())",context.Connection))command.ExecuteNonQuery();
        var inner=new RecordingDispatcher();
        var type=typeof(WorkflowSchema).Assembly.GetType("Aegis.Studio.Web.Operations.DemoSafeDeliveryDispatcher")!;
        var dispatcher=(IRetryableExternalDeliveryDispatcher)Activator.CreateInstance(type,context.Database,inner)!;
        var old=new ExternalDeliveryWorkItem(notification,[],MessageChannel.MsEmail,new MessageRequest{
            Title="Old event",Body="Old payload",Channels=MessageChannel.MsEmail,
            Metadata=new Dictionary<string,string>{{"StudioDatasetEpoch","old-dataset"}}},null,null,DateTimeOffset.UtcNow);
        await dispatcher.DeliverWithResultAsync(old);
        Assert.Equal(0,inner.Calls);
        await dispatcher.DeliverWithResultAsync(old with {Request=new MessageRequest{
            Title="New event",Body="New payload",Channels=MessageChannel.MsEmail,
            Metadata=new Dictionary<string,string>{{"StudioDatasetEpoch","new-dataset"}}}});
        Assert.Equal(1,inner.Calls);
    }
    private sealed class RecordingDispatcher:IRetryableExternalDeliveryDispatcher
    {
        public int Calls;
        public Task DeliverAsync(ExternalDeliveryWorkItem item,CancellationToken cancellationToken=default)
        {Calls++;return Task.CompletedTask;}
        public Task<ExternalDeliveryAttemptResult> DeliverWithResultAsync(ExternalDeliveryWorkItem item,CancellationToken cancellationToken=default)
        {Calls++;return Task.FromResult(new ExternalDeliveryAttemptResult(1,0,null));}
    }
}
