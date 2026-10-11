using Aegis.Studio.Web.WorkDay;
using Aegis.Studio.Web.Production;
using Aegis.Studio.Web.Scheduling;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;
namespace Aegis.Studio.Web.Tests;
public sealed class ManualPhotographerPersistenceTests
{
    [StudioPostgresFact]
    public void ManualReportsRetainRecorderAndWaitForEveryPhotographer()
    {
        using var context=new IntegratedWorkflowTests.WorkflowContext();var db=context.Database;var c=context.Connection;
        var workday=new WorkDayStore(db);workday.EnsureSchema();var photographers=new PhotographerStore(db);
        var a=photographers.Save(new("a","Photographer A","Employee",true,[],null));var b=photographers.Save(new("b","Photographer B","Employee",true,[],null));
        var start=DateTimeOffset.UtcNow.AddHours(1);var entry=new ScheduleStore(db,new ConfigurationBuilder().Build()).Create(new(context.Job,null,"Photography",start,start.AddHours(1),"Studio","Scheduled",null,[a.ScheduleResourceId,b.ScheduleResourceId]));
        var shoot=new OperationalWorkStore(db).CreateShoot(context.Job,new(entry.Id,"a","b",start,start.AddHours(1),"Studio",null,null),"Manager");
        Execute("create table studio_workflow_sessions(id uuid primary key,job_id uuid,shoot_id uuid,required boolean);create function studio_workflow_notify(uuid,text,text,text,text) returns void language plpgsql as 'begin return; end';");
        Execute("insert into studio_workflow_sessions values($1,$2,$3,true);update studio_jobs set status='Scheduled' where id=$2",Guid.NewGuid(),context.Job,shoot.Id);
        WorkDayOperationRequest Report(WorkDayOperationKind kind,long version)=>new(Guid.NewGuid(),kind,entry.Id,DateTimeOffset.UtcNow,"StudioManual",null,null,null,version);
        foreach(var person in new[]{"a","b"})
        {
            foreach(var step in new[]{WorkDayOperationKind.AcceptAssignment,WorkDayOperationKind.Arrive,WorkDayOperationKind.StartSession})
            {long version=step==WorkDayOperationKind.AcceptAssignment?0:step==WorkDayOperationKind.Arrive?1:2;Assert.Equal(WorkDayOperationState.Acknowledged,workday.Apply(person,Report(step,version),"manager","Reported by phone").State);}
            var completed=Report(WorkDayOperationKind.CompleteSession,3);
            Assert.Equal(WorkDayOperationState.Acknowledged,workday.Apply(person,completed,"manager","Reported by phone").State);
            Assert.Equal(WorkDayOperationState.Acknowledged,workday.Apply(person,completed,"manager","Reported by phone").State);
            Assert.Throws<InvalidOperationException>(()=>workday.Apply(person,completed,"other-manager","Reported by phone"));
            var current=new OperationalWorkStore(db).GetShoots(context.Job).Single();Assert.Equal(person=="b",current.CheckedOutAtUtc.HasValue);
        }
        using var q=new NpgsqlCommand("select count(*) from studio_workday_operations where recorded_by='manager' and recording_reason='Reported by phone'",c);Assert.Equal(8L,q.ExecuteScalar());
        void Execute(string sql,params object[] values){using var cmd=new NpgsqlCommand(sql,c);foreach(var value in values)cmd.Parameters.AddWithValue(value);cmd.ExecuteNonQuery();}
    }
}
