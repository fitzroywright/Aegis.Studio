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
        using var context=new IntegratedWorkflowTests.WorkflowContext();InitializeOutboxTestSchema(context.Connection);Guid id=Guid.NewGuid();
        using(var insert=new NpgsqlCommand("insert into studio_outbound_email(id,conversation_id,from_address,to_address,subject,body,status,created_at_utc,kind,sending_identity_type) select $1,conversation_id,'workspace@example.invalid','test@example.invalid','Test','Test','Pending',now(),'Reply','Workspace' from studio_jobs where id=$2",context.Connection)){insert.Parameters.AddWithValue(id);insert.Parameters.AddWithValue(context.Job);insert.ExecuteNonQuery();}
        var store=new OutboundEmailStore(context.Database,new WorkspaceStore(context.Database));var lease=Guid.NewGuid();Assert.NotNull(store.ClaimNext(["Email"],lease));
        var accepted=store.MarkProviderAccepted(id,null,lease)!;Assert.NotNull(accepted.ProviderAcceptedAtUtc);store.MarkVerificationFailure(id,"SentItemsUnavailable");
        Assert.Equal("ProviderAccepted",store.GetDelivery(id)!.Status);Assert.Equal(accepted.ProviderAcceptedAtUtc,store.GetDelivery(id)!.ProviderAcceptedAtUtc);Assert.Null(store.ClaimNext(["Email"],Guid.NewGuid()));Assert.Null(store.MarkFailure(id,"late-failure",DateTimeOffset.UtcNow));
    }
    [Fact]public void SharedContractExampleIsValidForStudioRosterConsumer()
    {
        var path=Path.Combine(AppContext.BaseDirectory,"contract-example.json");using var doc=JsonDocument.Parse(File.ReadAllText(path));
        var p=doc.RootElement.GetProperty("changeRecord");IntakeRosterStore.Validate(p.GetProperty("roster"));Assert.Equal("Website",p.GetProperty("source").GetString());Assert.Equal("2026-BR-000001",p.GetProperty("intakeReference").GetString());
    }
    [StudioPostgresFact]public void ReferenceAllocationUsesJamaicaYearAndPreservesWebsiteReference()
    {
        using var context=new IntegratedWorkflowTests.WorkflowContext();
        using var input=typeof(IntakeSchema).Assembly.GetManifestResourceStream("Aegis.Studio.Web.Intake.IntakeSchema.sql")!;using var reader=new StreamReader(input);
        string sql=reader.ReadToEnd().Split("create table if not exists studio_website_imports",StringSplitOptions.None)[0];
        using(var migration=new NpgsqlCommand(sql,context.Connection)){migration.ExecuteNonQuery();migration.ExecuteNonQuery();}
        Guid first=Guid.NewGuid(),second=Guid.NewGuid(),website=Guid.NewGuid();
        Insert(first,"2027-01-01T04:59:59Z",null);Insert(second,"2027-01-01T05:00:00Z",null);Insert(website,"2027-01-01T05:00:01Z","2027-W-000001");
        Assert.Equal("2026-S-000001",Reference(first));Assert.Equal("2027-S-000001",Reference(second));Assert.Equal("2027-W-000001",Reference(website));
        void Insert(Guid id,string at,string? reference){using var q=new NpgsqlCommand("insert into studio_conversations(id,workspace_id,subject,contact_name,contact_address,channel,created_at_utc,intake_reference) values($1,$2,'Test','Test','test@example.invalid','Manual',$3,$4)",context.Connection);q.Parameters.AddWithValue(id);q.Parameters.AddWithValue(context.Workspace);q.Parameters.AddWithValue(DateTimeOffset.Parse(at));q.Parameters.AddWithValue((object?)reference??DBNull.Value);q.ExecuteNonQuery();}
        string Reference(Guid id){using var q=new NpgsqlCommand("select intake_reference from studio_conversations where id=$1",context.Connection);q.Parameters.AddWithValue(id);return (string)q.ExecuteScalar()!;}
    }
    internal static void InitializeOutboxTestSchema(NpgsqlConnection c)
    {
        // This fixture isolates lease/acceptance persistence from the reminder scheduler.
        using var q=new NpgsqlCommand("""
          alter table studio_outbound_email add column if not exists studio_thread_id uuid;
          alter table studio_outbound_email add column if not exists studio_message_id uuid;
          alter table studio_outbound_email add column if not exists rfc_message_id text;
          alter table studio_outbound_email add column if not exists sender_mailbox text;
          alter table studio_outbound_email add column if not exists rfc_message_id_verified boolean not null default false;
          create or replace function studio_client_reminder_dispatchable(message uuid) returns boolean language sql as $$ select true $$;
          """,c);q.ExecuteNonQuery();
    }

}
