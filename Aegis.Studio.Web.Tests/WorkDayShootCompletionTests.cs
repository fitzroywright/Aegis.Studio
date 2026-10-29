using Aegis.Studio.Web.Media;
using Aegis.Studio.Web.Production;
using Aegis.Studio.Web.Scheduling;
using Aegis.Studio.Web.WorkDay;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class WorkDayShootCompletionTests
{
    [StudioPostgresFact]
    public void ImportedPhotosAllowExplicitCompletionWithoutInventingArrivalAndWaitForEveryPhotographer()
    {
        using var context=new IntegratedWorkflowTests.WorkflowContext();var db=context.Database;var c=context.Connection;
        var workday=new WorkDayStore(db);workday.EnsureSchema();var photographers=new PhotographerStore(db);
        var a=photographers.Save(new("a","Photographer A","Employee",true,[],null));
        var b=photographers.Save(new("b","Photographer B","Employee",true,[],null));
        var start=DateTimeOffset.UtcNow.AddHours(-2);
        var entry=new ScheduleStore(db,new ConfigurationBuilder().Build()).Create(new(context.Job,null,"Photography",start,start.AddHours(1),"Studio","Scheduled",null,[a.ScheduleResourceId,b.ScheduleResourceId]));
        var shoot=new OperationalWorkStore(db).CreateShoot(context.Job,new(entry.Id,"a","b",start,start.AddHours(1),"Studio",null,null),"Manager");
        Execute("create or replace function studio_workflow_notify(p_job uuid,p_event text,p_audience text,p_subject text,p_body text) returns void language plpgsql as 'begin return; end';");
        Execute("update studio_jobs set status='Scheduled' where id=$1",context.Job);
        Execute(MediaUploadReceipts.Schema);
        Execute("alter table studio_media_assets add column if not exists deleted_at_utc timestamptz");
        Guid Receipt(string person)
        {
            var id=Guid.NewGuid();string key=id.ToString("D");
            using var bytes=new MemoryStream([1,2,3]);
            var version=context.Versions.Add(context.Job,key,"images-2.jpeg","image/jpeg",bytes,person);
            using var asset=new NpgsqlCommand("select id from studio_media_assets where job_id=$1 and asset_key=$2",c);
            asset.Parameters.AddWithValue(context.Job);asset.Parameters.AddWithValue(key);Guid assetId=(Guid)asset.ExecuteScalar()!;
            string declaration=System.Text.Json.JsonSerializer.Serialize(new{note=$"Uploaded via WorkDay assignment {entry.Id:D}."});
            Execute("insert into studio_media_upload_batches(id,job_id,created_by,declaration) values($1,$2,$3,$4::jsonb)",id,context.Job,person,declaration);
            Execute("insert into studio_media_upload_receipts(id,batch_id,job_id,file_name,size_bytes,status,version_id,asset_id) values($1,$1,$2,'images-2.jpeg',3,'Stored',$3,$4)",id,context.Job,version.Id,assetId);
            return id;
        }
        var own=Receipt("a");var other=Receipt("b");
        long initial=workday.GetAssignmentState(entry.Id,"a")!.Value.Version;
        WorkDayOperationRequest Report(Guid upload)=>new(Guid.NewGuid(),WorkDayOperationKind.ConfirmShootComplete,entry.Id,DateTimeOffset.UtcNow,"workday-web","All photographs uploaded. Shoot done.",null,null,initial,[upload]);
        Assert.Throws<InvalidOperationException>(()=>workday.Apply("a",Report(other)));
        Assert.Throws<InvalidOperationException>(()=>workday.Apply("a",Report(Guid.NewGuid())));
        Execute("update studio_media_upload_receipts set status='Pending' where id=$1",own);
        Assert.Throws<InvalidOperationException>(()=>workday.Apply("a",Report(own)));
        Execute("update studio_media_upload_receipts set status='Stored' where id=$1",own);
        var report=Report(own);
        Assert.Equal(WorkDayOperationState.Acknowledged,workday.Apply("a",report).State);
        Assert.Equal(WorkDayOperationState.Acknowledged,workday.Apply("a",report).State);
        Assert.Throws<InvalidOperationException>(()=>workday.Apply("a",report with{UploadIds=[other]}));
        var first=new OperationalWorkStore(db).GetShoots(context.Job).Single();
        Assert.Null(first.CheckedOutAtUtc);Assert.Equal("Completed",workday.GetAssignmentState(entry.Id,"a")!.Value.Status);
        Assert.Equal(WorkDayOperationState.Acknowledged,workday.Apply("b",Report(other)).State);
        var finished=new OperationalWorkStore(db).GetShoots(context.Job).Single();Assert.NotNull(finished.CheckedOutAtUtc);
        using var check=new NpgsqlCommand("select checked_in_at_utc,status from studio_shoot_sessions where id=$1",c);
        check.Parameters.AddWithValue(shoot.Id);using(var reader=check.ExecuteReader()){Assert.True(reader.Read());Assert.True(reader.IsDBNull(0));Assert.Equal("ShootComplete",reader.GetString(1));}
        using var messages=new NpgsqlCommand("select count(*) from studio_messages where body like '%Their shoot is done.%'",c);Assert.Equal(2L,messages.ExecuteScalar());
        Execute("update studio_jobs set status='Completed' where id=$1",context.Job);
        Assert.Equal(WorkDayOperationState.Conflict,workday.Apply("a",Report(own) with{ExpectedVersion=initial+1}).State);

        void Execute(string sql,params object[] values){using var cmd=new NpgsqlCommand(sql,c);foreach(var value in values)cmd.Parameters.AddWithValue(value);cmd.ExecuteNonQuery();}
    }

    [Fact]
    public void LegacyOperationJsonDoesNotGainAnEmptyUploadList()
    {
        var request=new WorkDayOperationRequest(Guid.NewGuid(),WorkDayOperationKind.AddNote,Guid.NewGuid(),DateTimeOffset.UtcNow,"device","note",null,null,0);
        Assert.DoesNotContain("UploadIds",System.Text.Json.JsonSerializer.Serialize(request));
    }
}
