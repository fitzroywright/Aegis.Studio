using Aegis.Studio.Web.Reminders;
using Npgsql;
using Xunit;
namespace Aegis.Studio.Web.Tests;
public sealed class ClientReminderTests
{
    [Fact] public void QuietHoursUseTheConfiguredTimeZone()
    {
        var zone=TimeZoneInfo.FindSystemTimeZoneById("America/Jamaica");
        Assert.True(ClientReminderRules.IsQuiet(DateTimeOffset.Parse("2026-10-18T13:59:00Z"),zone));
        Assert.False(ClientReminderRules.IsQuiet(DateTimeOffset.Parse("2026-10-18T14:00:00Z"),zone));
        Assert.False(ClientReminderRules.IsQuiet(DateTimeOffset.Parse("2026-10-18T22:59:00Z"),zone));
        Assert.True(ClientReminderRules.IsQuiet(DateTimeOffset.Parse("2026-10-18T23:00:00Z"),zone));
    }
    [Fact] public void TemplatesRenderValuesWithoutExpandingClientTextAsAnotherPlaceholder()
    {
        Assert.Equal("Hello {job}, Portrait",ClientReminderRules.Render("Hello {client}, {job}","{job}","Portrait","",""));
        Assert.Throws<ArgumentException>(()=>ClientReminderRules.Validate("Shoot","Email","Session","Hello {password}"));
        Assert.Throws<ArgumentException>(()=>ClientReminderRules.Validate("Shoot","SMS","Session","Hello"));
    }
    [StudioPostgresFact] public void CurrentBusinessStateAndPreferencesGateDispatch()
    {
        using var c=new NpgsqlConnection(Environment.GetEnvironmentVariable("STUDIO_TEST_POSTGRES"));c.Open();using var tx=c.BeginTransaction();
        // Every object is transaction-local to an isolated schema; disposal rolls it all back.
        string schema="reminder_test_"+Guid.NewGuid().ToString("N");
        Run($"create schema {schema}; set local search_path to {schema};");
        Run(Fixture);
        using var input=typeof(ClientReminderStore).Assembly.GetManifestResourceStream("Aegis.Studio.Web.Reminders.ClientReminders.sql")!;
        using var reader=new StreamReader(input);string migration=reader.ReadToEnd();
        Run(migration);Run(migration); // Startup must work on a fresh database and on restart.
        Run("""
          insert into studio_jobs values('11111111-1111-1111-1111-111111111111','Scheduled',now(),now(),null,'22222222-2222-2222-2222-222222222222','Portrait');
          insert into studio_conversations values('22222222-2222-2222-2222-222222222222','andrea@example.test');
          insert into studio_shoot_sessions values('33333333-3333-3333-3333-333333333333','11111111-1111-1111-1111-111111111111',now()+interval '2 days','Studio A','Scheduled');
          insert into studio_client_reminder_preferences(job_id,email_allowed,updated_by) values('11111111-1111-1111-1111-111111111111',true,'staff');
          insert into studio_client_reminders(id,job_id,kind,source_id,anchor_at_utc,scheduled_at_utc,channel,recipient,subject,body,status,created_by,message_id,source_detail,source_location)
          select '44444444-4444-4444-4444-444444444444',job_id,kind,source_id,anchor_at_utc,now(),'Email','andrea@example.test','Session','Reminder','Queued','staff','44444444-4444-4444-4444-444444444444',detail,location from studio_client_reminder_sources where kind='Shoot';
          """);
        Assert.True(Dispatchable());
        Run("update studio_client_reminder_preferences set email_allowed=false");Assert.False(Dispatchable());
        Run("update studio_client_reminder_preferences set email_allowed=true; update studio_conversations set contact_address='changed@example.test'");Assert.False(Dispatchable());
        Run("update studio_conversations set contact_address='andrea@example.test'; update studio_shoot_sessions set call_time_utc=call_time_utc+interval '1 day'");Assert.False(Dispatchable());
        Run("update studio_shoot_sessions set call_time_utc=call_time_utc-interval '1 day'; update studio_jobs set status='Cancelled'");Assert.False(Dispatchable());
        Run("""
          update studio_jobs set status='Pending';
          insert into studio_frontdesk_requests values('55555555-5555-5555-5555-555555555555','11111111-1111-1111-1111-111111111111',now(),'AwaitingDeposit','{"requiredDeposit":100,"currency":"JMD"}');
          """);
        Assert.Equal(1L,Count("Deposit"));
        Run("insert into studio_frontdesk_deposits values('55555555-5555-5555-5555-555555555555',100)");Assert.Equal(0L,Count("Deposit"));
        Run("update studio_jobs set status='Ready for Delivery'");Assert.Equal(1L,Count("Collection"));
        Run("insert into studio_job_fulfillment values('11111111-1111-1111-1111-111111111111')");Assert.Equal(0L,Count("Collection"));
        Run("insert into studio_job_activities values('66666666-6666-6666-6666-666666666666','11111111-1111-1111-1111-111111111111',now(),'Approve client layout','WaitingOnClient')");Assert.Equal(1L,Count("Approval"));
        Run("update studio_job_activities set status='Completed'");Assert.Equal(0L,Count("Approval"));
        void Run(string sql){using var q=new NpgsqlCommand(sql,c,tx);q.ExecuteNonQuery();}
        bool Dispatchable(){using var q=new NpgsqlCommand("select studio_client_reminder_dispatchable('44444444-4444-4444-4444-444444444444')",c,tx);return (bool)q.ExecuteScalar()!;}
        long Count(string kind){using var q=new NpgsqlCommand("select count(*) from studio_client_reminder_sources where kind=$1",c,tx);q.Parameters.AddWithValue(kind);return(long)q.ExecuteScalar()!;}
    }
    public const string Fixture="""
      create table studio_jobs(id uuid primary key,status text,created_at_utc timestamptz,stage_entered_at_utc timestamptz,client_id uuid,conversation_id uuid,title text);
      create table studio_shoot_sessions(id uuid,job_id uuid,call_time_utc timestamptz,location text,status text);
      create table studio_frontdesk_requests(id uuid,job_id uuid,created_at_utc timestamptz,status text,payload jsonb);
      create table studio_frontdesk_deposits(request_id uuid,amount numeric);
      create table studio_job_activities(id uuid,job_id uuid,updated_at_utc timestamptz,title text,status text);
      create table studio_workflow_history(job_id uuid,new_stage text,entered_at_utc timestamptz);
      create table studio_job_fulfillment(job_id uuid);
      create table studio_clients(id uuid,email text,phone text);
      create table studio_conversations(id uuid,contact_address text);
      """;
}

