using System.Text.Json;
using Aegis.Studio.Web.WorkDay;
using Aegis.Studio.Web.Scheduling;
using Aegis.Studio.Web.Production;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;
namespace Aegis.Studio.Web.Tests;

public sealed class WorkDayFieldSyncTests
{
    [Theory]
    [InlineData("Paid")]
    [InlineData("Approved")]
    public void FieldReportsCannotExpressFinancialOrProductionApproval(string status)
    {
        Assert.Throws<InvalidOperationException>(()=>WorkDayFieldReports.Parse(JsonSerializer.Serialize(new WorkDayFieldStatus("job",status,new string('a',32),0))));
    }

    [Theory]
    [InlineData("Absent")]
    [InlineData("Deferred")]
    [InlineData("Cancelled")]
    public void ExceptionsRequireAnExplanation(string status)
    {
        Assert.Throws<InvalidOperationException>(()=>WorkDayFieldReports.Parse(JsonSerializer.Serialize(new WorkDayFieldStatus("subject:"+Guid.NewGuid(),status,new string('a',32),0))));
    }

    [StudioPostgresFact]
    public void TwoAssignmentsShareRosterRevisionAndCannotOverwriteEachOther()
    {
        using var context=new IntegratedWorkflowTests.WorkflowContext();var c=context.Connection;var workday=new WorkDayStore(context.Database);workday.EnsureSchema();
        using(var q=new NpgsqlCommand("update studio_jobs set status='Scheduled' where id=$1;create function studio_workflow_notify(uuid,text,text,text,text) returns void language plpgsql as 'begin return; end';",c)){q.Parameters.AddWithValue(context.Job);q.ExecuteNonQuery();}
        var profile=new PhotographerStore(context.Database).Save(new("a","Photographer A","Employee",true,[],null));
        var scheduler=new ScheduleStore(context.Database,new ConfigurationBuilder().Build());var start=DateTimeOffset.UtcNow.AddDays(1);
        var first=scheduler.Create(new(context.Job,null,"First",start,start.AddHours(1),"Studio","Scheduled",null,[profile.ScheduleResourceId]));
        var second=scheduler.Create(new(context.Job,null,"Second",start.AddDays(1),start.AddDays(1).AddHours(1),"Studio","Scheduled",null,[profile.ScheduleResourceId]));
        Guid subject=Guid.NewGuid();using(var q=new NpgsqlCommand("insert into studio_job_subjects(id,job_id,name,updated_by) values($1,$2,'Person','Manager')",c)){q.Parameters.AddWithValue(subject);q.Parameters.AddWithValue(context.Job);q.ExecuteNonQuery();}
        foreach(var id in new[]{first.Id,second.Id})Assert.Equal(WorkDayOperationState.Acknowledged,workday.Apply("a",new(Guid.NewGuid(),WorkDayOperationKind.AcceptAssignment,id,DateTimeOffset.UtcNow,"device",null,null,null,0)).State);
        var cached=WorkDayFieldReports.Targets(c,null,second.Id).Single(x=>x.TargetId=="subject:"+subject);
        WorkDayOperationRequest Report(Guid assignment,string status)=>new(Guid.NewGuid(),WorkDayOperationKind.RecordFieldStatus,assignment,DateTimeOffset.UtcNow,"device",status,JsonSerializer.Serialize(new WorkDayFieldStatus(cached.TargetId,status,cached.SourceFingerprint,0,"Observation")),null,1);
        Assert.Equal(WorkDayOperationState.Acknowledged,workday.Apply("a",Report(first.Id,"Photographed")).State);
        Assert.Equal(WorkDayOperationState.Conflict,workday.Apply("a",Report(second.Id,"Absent")).State);
        var current=WorkDayFieldReports.Targets(c,null,second.Id).Single(x=>x.TargetId==cached.TargetId);Assert.Equal(1L,current.Version);Assert.Equal("Photographed",current.Status);
    }

    [StudioPostgresFact]
    public void LostAcknowledgementReplaysOnceAndNewStudioRosterEditsConflict()
    {
        using var context=new IntegratedWorkflowTests.WorkflowContext();var c=context.Connection;var db=context.Database;
        var workday=new WorkDayStore(db);workday.EnsureSchema();
        var photographer=new PhotographerStore(db).Save(new("a","Photographer A","Employee",true,[],null));
        var start=DateTimeOffset.UtcNow.AddHours(1);
        var entry=new ScheduleStore(db,new ConfigurationBuilder().Build()).Create(new(context.Job,null,"Photography",start,start.AddHours(1),"Studio","Scheduled",null,[photographer.ScheduleResourceId]));
        Execute("create table field_notifications(audience text);create function studio_workflow_notify(uuid,text,text,text,text) returns void language plpgsql as 'begin insert into field_notifications values($3); end';");
        Execute("update studio_jobs set status='Scheduled' where id=$1",context.Job);
        Guid subject=Guid.NewGuid();Execute("insert into studio_job_subjects(id,job_id,name,reference,updated_by) values($1,$2,'Roster pupil','P1','Manager')",subject,context.Job);
        var roster=WorkDayFieldReports.Targets(c,null,entry.Id).Single(x=>x.TargetId=="subject:"+subject);
        WorkDayOperationRequest Report(WorkDayFieldTarget target,string state,long revision)=>new(Guid.NewGuid(),WorkDayOperationKind.RecordFieldStatus,entry.Id,DateTimeOffset.UtcNow,"Device",$"{target.Name}: {state}",JsonSerializer.Serialize(new WorkDayFieldStatus(target.TargetId,state,target.SourceFingerprint,revision,"Field observation")),null,1);
        Assert.Equal(WorkDayOperationState.Acknowledged,workday.Apply("a",new(Guid.NewGuid(),WorkDayOperationKind.AcceptAssignment,entry.Id,DateTimeOffset.UtcNow,"Device",null,null,null,0)).State);
        var completed=Report(roster,"Completed",0);
        Assert.Equal(WorkDayOperationState.Acknowledged,workday.Apply("a",completed).State);
        // A dropped response is retried with the original ID and exact original facts.
        Assert.Equal(WorkDayOperationState.Acknowledged,workday.Apply("a",completed).State);
        Assert.Equal(2L,Scalar("select count(*) from studio_workday_operations"));
        Assert.Equal(1L,Scalar("select count(*) from studio_workday_notes"));
        Assert.Equal(4L,Scalar("select count(*) from field_notifications where audience in ('Owner','Manager')"));
        Assert.Throws<InvalidOperationException>(()=>workday.Apply("a",completed with{Text="Changed facts"}));
        var cached=WorkDayFieldReports.Targets(c,null,entry.Id).Single(x=>x.TargetId==roster.TargetId);
        Execute("update studio_job_subjects set notes='New Studio instructions' where id=$1",subject);
        var deferred=Report(cached,"Deferred",cached.Version);
        Assert.Equal(WorkDayOperationState.Conflict,workday.Apply("a",deferred).State);
        Assert.Equal(WorkDayOperationState.Conflict,workday.Apply("a",deferred).State);
        Assert.Equal(1L,Scalar("select count(*) from studio_workday_field_conflicts"));
        Assert.Equal("Completed",Scalar("select status from studio_workday_field_status where target_id=$1",roster.TargetId));
        // Replaying committed work still works after newer roster changes.
        Assert.Equal(WorkDayOperationState.Acknowledged,workday.Apply("a",completed).State);
        Assert.Equal(2L,Scalar("select count(*) from studio_workday_operations"));
        void Execute(string sql,params object[] values){using var q=new NpgsqlCommand(sql,c);foreach(var v in values)q.Parameters.AddWithValue(v);q.ExecuteNonQuery();}
        object? Scalar(string sql,params object[] values){using var q=new NpgsqlCommand(sql,c);foreach(var v in values)q.Parameters.AddWithValue(v);return q.ExecuteScalar();}
    }
}
