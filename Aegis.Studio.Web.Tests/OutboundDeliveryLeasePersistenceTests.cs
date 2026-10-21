using Aegis.Studio.Web.Email;
using Aegis.Studio.Web.Operations;
using Npgsql;
using Xunit;
namespace Aegis.Studio.Web.Tests;
public sealed class OutboundDeliveryLeasePersistenceTests
{
    [StudioPostgresFact]
    public void ExpiredClaimIsHeldInsteadOfBlindlyResent()
    {
        using var context=new IntegratedWorkflowTests.WorkflowContext();IntakeCompletionTests.InitializeOutboxTestSchema(context.Connection);var id=Guid.NewGuid();
        using(var insert=new NpgsqlCommand("insert into studio_outbound_email(id,conversation_id,from_address,to_address,subject,body,thread_id,status,created_at_utc,kind,sending_identity_type) select $1,conversation_id,'workspace@example.test','client@example.test','Deposit','Request',conversation_id::text,'Pending',now(),'DepositRequest','Workspace' from studio_jobs where id=$2",context.Connection))
        {insert.Parameters.AddWithValue(id);insert.Parameters.AddWithValue(context.Job);insert.ExecuteNonQuery();}
        var outbox=new OutboundEmailStore(context.Database,new WorkspaceStore(context.Database));var oldLease=Guid.NewGuid();var nextLease=Guid.NewGuid();
        Assert.Equal(id,outbox.ClaimNext(["Email"],oldLease)!.Id);Assert.Null(outbox.ClaimNext(["Email"],nextLease));
        using(var expire=new NpgsqlCommand("update studio_outbound_email set lease_until_utc=now()-interval '1 second' where id=$1",context.Connection)){expire.Parameters.AddWithValue(id);expire.ExecuteNonQuery();}
        Assert.Null(outbox.ClaimNext(["Email"],nextLease));
        Assert.Null(outbox.MarkProviderAccepted(id,null,oldLease));
        Assert.Equal("Attention",outbox.GetDelivery(id)!.Status);
        Assert.Null(outbox.ClaimNext(["Email"],Guid.NewGuid()));
    }
}
