using System.Security.Claims;
using Aegis.Studio.Web.FrontDesk;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Workflow;
using Npgsql;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class NextActionPersistenceTests
{
    private static ClaimsPrincipal User(string name,bool admin=false)=>new(new ClaimsIdentity(
        admin?[new Claim("login_name",name),new Claim(ClaimTypes.Role,"Administrator")]:[new Claim("login_name",name)],"Test"));
    private static void Sql(NpgsqlConnection c,string text,params object[] values)
    {using var q=new NpgsqlCommand(text,c);foreach(var value in values)q.Parameters.AddWithValue(value);q.ExecuteNonQuery();}
    private static NextActionStore Prepare(IntegratedWorkflowTests.WorkflowContext ctx)
    {
        WorkflowSchema.Ensure(ctx.Database);new GlobalOperationalRoleStore(ctx.Database).EnsureSchema();
        new FrontDeskBookingRequestStore(ctx.Database).EnsureSchema();new StudioQuoteStore(ctx.Database).Migrate();new StudioEmployeeDirectoryStore(ctx.Database).EnsureSchema();
        NextActionStore.EnsureSchema(ctx.Database);NextActionStore.EnsureSchema(ctx.Database);
        Sql(ctx.Connection,"alter table studio_conversations add column if not exists client_id uuid");
        Guid client=Guid.NewGuid();Sql(ctx.Connection,"insert into studio_clients(id,display_name,updated_at_utc) values($1,'Client',now())",client);
        Sql(ctx.Connection,"update studio_jobs set client_id=$2,status='Pending' where id=$1",ctx.Job,client);
        Sql(ctx.Connection,"insert into studio_employees(user_name,display_name) values('lisa','Lisa')");
        return new(ctx.Database,new JobAccessService(ctx.Database),new WorkspaceAccessService(ctx.Database));
    }
    [StudioPostgresFact] public void DetailsPersistWithVersionAuditAndDoNotCarryIntoTheFollowingAction()
    {
        using var ctx=new IntegratedWorkflowTests.WorkflowContext();var store=Prepare(ctx);var admin=User("admin",true);
        var row=Assert.Single(store.List(admin,"job",ctx.Job));Assert.Equal("confirm-booking",row.Action.Key);Assert.Null(row.DueAtUtc);
        DateTimeOffset due=DateTimeOffset.UtcNow.AddDays(1);
        var saved=store.Save(admin,"job",ctx.Job,new(row.Action.Key,"lisa",due,"Awaiting client approval",0));
        Assert.Equal(1,saved.Version);Assert.Equal("lisa",saved.AssignedTo);Assert.Equal("Awaiting client approval",saved.WaitingReason);Assert.Equal(due.ToUnixTimeSeconds(),saved.DueAtUtc!.Value.ToUnixTimeSeconds());
        Assert.Throws<WorkflowConflictException>(()=>store.Save(admin,"job",ctx.Job,new(row.Action.Key,"lisa",null,null,0)));
        Sql(ctx.Connection,"update studio_jobs set status='Needs Assignment' where id=$1",ctx.Job);
        var next=Assert.Single(store.List(admin,"job",ctx.Job));Assert.Equal("assign-owner",next.Action.Key);Assert.Null(next.DueAtUtc);Assert.Null(next.WaitingReason);Assert.Equal(0,next.Version);
        using var audit=new NpgsqlCommand("select count(*) from studio_next_action_audit",ctx.Connection);Assert.Equal(1L,audit.ExecuteScalar());
    }
    [StudioPostgresFact] public void InvisibleRecordsAreExcludedAndViewersCannotChangeResponsibility()
    {
        using var ctx=new IntegratedWorkflowTests.WorkflowContext();var store=Prepare(ctx);var guest=User("guest");
        Assert.Empty(store.List(guest));
        Sql(ctx.Connection,"insert into studio_workspace_members(workspace_id,user_name,display_name,created_at_utc,updated_at_utc) values($1,'reader','Reader',now(),now())",ctx.Workspace);
        var reader=User("reader");var row=Assert.Single(store.List(reader,"job",ctx.Job));Assert.False(row.CanManage);
        Assert.Throws<UnauthorizedAccessException>(()=>store.Save(reader,"job",ctx.Job,new(row.Action.Key,"lisa",null,null,0)));
        Assert.Throws<ArgumentException>(()=>store.Save(User("admin",true),"job",ctx.Job,new(row.Action.Key,"unknown.person",null,null,0)));
    }
    [StudioPostgresFact] public void EnquiryReplyAndInternalNotesHaveDifferentEffectsOnTheNextAction()
    {
        using var ctx=new IntegratedWorkflowTests.WorkflowContext();var store=Prepare(ctx);var admin=User("admin",true);Guid enquiry=Guid.NewGuid(),client=Guid.NewGuid();
        Sql(ctx.Connection,"insert into studio_clients(id,display_name,updated_at_utc) values($1,'School',now())",client);
        Sql(ctx.Connection,"insert into studio_conversations(id,workspace_id,subject,contact_name,contact_address,channel,created_at_utc,client_id) values($1,$2,'Graduation','School','school@example.test','Email',now(),$3)",enquiry,ctx.Workspace,client);
        void Message(string visibility,string author,int minutes)=>Sql(ctx.Connection,"insert into studio_messages(id,conversation_id,author_user_name,author_display_name,visibility,body,created_at_utc) values($1,$2,$3,'Author',$4,'Message',now()+make_interval(mins=>$5))",Guid.NewGuid(),enquiry,author,visibility,minutes);
        Message("External","school@example.test",0);
        Assert.Equal("reply",Assert.Single(store.List(admin,"enquiry",enquiry)).Action.Key);
        Message("Internal","lisa",1);Assert.Equal("reply",Assert.Single(store.List(admin,"enquiry",enquiry)).Action.Key);
        Message("External","lisa",2);Assert.Equal("requirements",Assert.Single(store.List(admin,"enquiry",enquiry)).Action.Key);
        Message("External","school@example.test",3);Assert.Equal("reply",Assert.Single(store.List(admin,"enquiry",enquiry)).Action.Key);
        Sql(ctx.Connection,"update studio_conversations set is_closed=true where id=$1",enquiry);Assert.Empty(store.List(admin,"enquiry",enquiry));
    }
}
