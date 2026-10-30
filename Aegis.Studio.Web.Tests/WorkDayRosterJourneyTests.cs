using System.Security.Claims;
using System.Text.Json;
using Aegis.Studio.Web.FrontDesk;
using Aegis.Studio.Web.Galleries;
using Aegis.Studio.Web.Media;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Production;
using Aegis.Studio.Web.Scheduling;
using Aegis.Studio.Web.WorkDay;
using Aegis.Studio.Web.Workflow;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;
namespace Aegis.Studio.Web.Tests;

public sealed class WorkDayRosterJourneyTests
{
    // Server contract journey. Physical offline storage/reconnect remains a device acceptance gate.
    [StudioPostgresFact]
    public void AssignAcceptReportReplayCountAndScheduleOnlyRemainder()
    {
        using var context=new IntegratedWorkflowTests.WorkflowContext();var db=context.Database;var c=context.Connection;
        new GlobalOperationalRoleStore(db).EnsureSchema();
        new PhotographerStore(db).Save(new("photo","Photographer","Employee",true,[],null));
        var employees=new StudioEmployeeDirectoryStore(db);employees.EnsureSchema();employees.Save(new("owner","Owner",null));
        new FrontDeskBookingRequestStore(db).EnsureSchema();WorkflowSchema.Ensure(db);
        var workday=new WorkDayStore(db);workday.EnsureSchema();
        var workflow=new WorkflowStore(db,new JobAccessService(db),new WorkspaceAccessService(db),new UnusedPublisher(),new ConfigurationBuilder().Build());
        ClaimsPrincipal Actor(string name)=>new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,name),new Claim(ClaimTypes.Role,"Administrator")],"Test"));
        var manager=Actor("manager");var owner=Actor("owner");
        Execute("select set_config('studio.connected_command','true',false)");
        Execute("update studio_jobs set status='Needs Assignment' where id=$1",context.Job);
        Execute("select set_config('studio.connected_command','false',false)");
        JsonElement Snapshot()=>WorkflowStore.Snapshot(c,null,context.Job);
        long Revision()=>Snapshot().GetProperty("revision").GetInt64();
        workflow.Execute(context.Job,new("owner",Revision(),Guid.NewGuid(),UserName:"owner"),manager);
        Assert.Throws<ArgumentException>(()=>workflow.Execute(context.Job,new("session",Revision(),Guid.NewGuid(),Title:"Premature",Crew:[new("photo","Lead")]),manager));
        workflow.Execute(context.Job,new("accept-owner",Revision(),Guid.NewGuid()),owner);
        var start=DateTimeOffset.UtcNow.AddDays(2);
        workflow.Execute(context.Job,new("session",Revision(),Guid.NewGuid(),SessionId:Snapshot().GetProperty("sessions")[0].GetProperty("id").GetGuid(),Title:"Original",Crew:[new("photo","Lead")],Start:start,End:start.AddHours(1),Location:"Studio",Reason:"Instructions"),owner);
        var original=Snapshot().GetProperty("sessions").EnumerateArray().Single(x=>x.GetProperty("shoot").ValueKind==JsonValueKind.Object).GetProperty("shoot").GetProperty("schedule_entry_id").GetGuid();
        var photographed=Guid.NewGuid();var deferred=Guid.NewGuid();
        Execute("insert into studio_job_subjects(id,job_id,name,reference,updated_by,collection_name) values($1,$3,'One','P1','manager','Portraits'),($2,$3,'Two','P2','manager','Portraits')",photographed,deferred,context.Job);
        long initial=workday.GetAssignmentState(original,"photo")!.Value.Version;
        WorkDayOperationRequest Step(WorkDayOperationKind kind,long version)=>new(Guid.NewGuid(),kind,original,DateTimeOffset.UtcNow,"device",null,null,null,initial+version);
        Assert.Equal(WorkDayOperationState.Acknowledged,workday.Apply("photo",Step(WorkDayOperationKind.AcceptAssignment,0)).State);
        WorkDayOperationRequest Report(Guid id,string state)
        {
            var target=WorkDayFieldReports.Targets(c,null,original).Single(x=>x.TargetId=="subject:"+id);
            return new(Guid.NewGuid(),WorkDayOperationKind.RecordFieldStatus,original,DateTimeOffset.UtcNow,"device",state,JsonSerializer.Serialize(new WorkDayFieldStatus(target.TargetId,state,target.SourceFingerprint,target.Version,"Field observation")),null,initial+1);
        }
        // Prepared reports retain their original IDs and revisions through delayed transmission.
        var first=Report(photographed,"Photographed");var second=Report(deferred,"Deferred");
        Assert.Equal(WorkDayOperationState.Acknowledged,workday.Apply("photo",first).State);
        Assert.Equal(WorkDayOperationState.Acknowledged,workday.Apply("photo",second).State);
        Assert.Equal(WorkDayOperationState.Acknowledged,workday.Apply("photo",first).State);
        Assert.Equal(WorkDayOperationState.Conflict,workday.Apply("unassigned-user",first with{OperationId=Guid.NewGuid()}).State);
        var counts=new MediaProductionStore(db,context.Versions).Snapshot(context.Job).GetProperty("rosterCounts");
        Assert.Equal(1,counts.GetProperty("Photographed").GetInt32());Assert.Equal(1,counts.GetProperty("Deferred").GetInt32());
        foreach(var (kind,version) in new[]{(WorkDayOperationKind.Arrive,1L),(WorkDayOperationKind.StartSession,2L),(WorkDayOperationKind.CompleteSession,3L)})
            Assert.Equal(WorkDayOperationState.Acknowledged,workday.Apply("photo",Step(kind,version)).State);
        Assert.Equal("Awaiting Upload",Snapshot().GetProperty("stage").GetString());
        var followup=new WorkflowCommand("reschedule-remainder",Revision(),Guid.NewGuid(),Title:"Remainder",Crew:[new("photo","Lead")],Start:start.AddDays(1),End:start.AddDays(1).AddHours(1),Location:"Studio",Reason:"Return for deferred subject",SubjectIds:[deferred]);
        workflow.Execute(context.Job,followup,owner);workflow.Execute(context.Job,followup,owner);
        Assert.Equal("Scheduled",Snapshot().GetProperty("stage").GetString());
        var shoots=new OperationalWorkStore(db).GetShoots(context.Job);Assert.Equal(2,shoots.Count);
        Assert.NotNull(shoots.Single(x=>x.ScheduleEntryId==original).CheckedOutAtUtc);
        var next=shoots.Single(x=>x.ScheduleEntryId!=original).ScheduleEntryId;
        var targets=WorkDayFieldReports.Targets(c,null,next!.Value).Where(x=>x.Scope=="Roster").ToArray();
        Assert.Single(targets);Assert.Equal("subject:"+deferred,targets[0].TargetId);
        Assert.Throws<ArgumentException>(()=>workflow.Execute(context.Job,followup with{OperationId=Guid.NewGuid(),ExpectedRevision=Revision(),SubjectIds=[photographed]},owner));
        void Execute(string sql,params object[] values){using var q=new NpgsqlCommand(sql,c);foreach(var v in values)q.Parameters.AddWithValue(v);q.ExecuteNonQuery();}
    }
    private sealed class UnusedPublisher:IClientGalleryPublisher
    {public Task<ClientGalleryPublication> PublishAsync(ClientGalleryPublishRequest request,CancellationToken token)=>throw new InvalidOperationException("This journey does not publish galleries.");}
}
