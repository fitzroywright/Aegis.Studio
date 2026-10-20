using System.Text.Json;
using Aegis.Studio.Web.Intake;
using Aegis.Studio.Web.Email;
using Aegis.Studio.Web.Operations;
using Npgsql;
using Xunit;
namespace Aegis.Studio.Web.Tests;
public sealed class IntakeCompletionTests
{
    [Fact]public void NotificationSelectionRequiresContactButOptionalContactsDoNotBlockRoster()
    {
        IntakeRosterStore.Validate(JsonSerializer.Deserialize<JsonElement>("""[{"name":"Student","reference":"one","pickupNotification":"None"}]"""));
        Assert.Throws<ArgumentException>(()=>IntakeRosterStore.Validate(JsonSerializer.Deserialize<JsonElement>("""[{"name":"Student","reference":"one","pickupNotification":"Email"}]""")));
        Assert.Throws<ArgumentException>(()=>IntakeRosterStore.Validate(JsonSerializer.Deserialize<JsonElement>("""[{"name":"Unclassified","reference":"system"}]""")));
    }
    [Fact]public void AttachmentMagicMustAgreeWithTypeAndExtension()
    {
        Assert.False(WebsiteAttachmentSafety.TypeMatches("executable"u8.ToArray(),"image/jpeg","photo.jpg"));
        Assert.False(WebsiteAttachmentSafety.TypeMatches("%PDF-test"u8.ToArray(),"application/pdf","photo.jpg"));
        Assert.True(WebsiteAttachmentSafety.TypeMatches("%PDF-test"u8.ToArray(),"application/pdf","document.pdf"));
    }
    [StudioPostgresFact]public void AcceptedDeliveryRemainsAcceptedAfterThreadingFailureAndCannotBeClaimedAgain()
    {
        using var context=new IntegratedWorkflowTests.WorkflowContext();Guid id=Guid.NewGuid();
        using(var insert=new NpgsqlCommand("insert into studio_outbound_email(id,conversation_id,from_address,to_address,subject,body,status,created_at_utc,kind,sending_identity_type) select $1,conversation_id,'workspace@example.invalid','test@example.invalid','Test','Test','Pending',now(),'Reply','Workspace' from studio_jobs where id=$2",context.Connection)){insert.Parameters.AddWithValue(id);insert.Parameters.AddWithValue(context.Job);insert.ExecuteNonQuery();}
        var store=new OutboundEmailStore(context.Database,new WorkspaceStore(context.Database));var lease=Guid.NewGuid();Assert.NotNull(store.ClaimNext(["Email"],lease));
        var accepted=store.MarkProviderAccepted(id,null,lease)!;Assert.NotNull(accepted.ProviderAcceptedAtUtc);store.MarkVerificationFailure(id,"SentItemsUnavailable");
        Assert.Equal("ProviderAccepted",store.GetDelivery(id)!.Status);Assert.Equal(accepted.ProviderAcceptedAtUtc,store.GetDelivery(id)!.ProviderAcceptedAtUtc);Assert.Null(store.ClaimNext(["Email"],Guid.NewGuid()));Assert.Null(store.MarkFailure(id,"late-failure",DateTimeOffset.UtcNow));
    }
    [Fact]public void SharedContractExampleIsValidForStudioRosterConsumer()
    {
        var path=Path.Combine(AppContext.BaseDirectory,"contract-example.json");using var doc=JsonDocument.Parse(File.ReadAllText(path));
        var p=doc.RootElement.GetProperty("changeRecord");IntakeRosterStore.Validate(p.GetProperty("roster"));Assert.Equal("Website",p.GetProperty("source").GetString());Assert.Equal("2026-W-000001",p.GetProperty("intakeReference").GetString());
    }
}
