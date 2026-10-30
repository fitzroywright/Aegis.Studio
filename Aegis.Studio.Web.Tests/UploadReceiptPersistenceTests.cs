using Aegis.Studio.Web.Media;
using Microsoft.AspNetCore.Http;
using Npgsql;
using System.Text.Json;
using Xunit;
namespace Aegis.Studio.Web.Tests;
public sealed class UploadReceiptPersistenceTests
{
    [StudioPostgresFact]
    public void LostResponseReplaysStoredVersionAndRejectsChangedBytes()
    {
        using var context=new IntegratedWorkflowTests.WorkflowContext();
        using(var schema=new NpgsqlCommand("create table studio_workflow_sessions(id uuid primary key,job_id uuid,required boolean);create table studio_workflow_crew(session_id uuid,user_name text);",context.Connection))schema.ExecuteNonQuery();
        var store=new MediaUploadReceipts(context.Database,new MediaProductionStore(context.Database,context.Versions));
        var id=Guid.NewGuid();var batch=Guid.NewGuid();
        var declaration=new UploadBatchDeclaration(batch,null,[new(id,"photo.jpg",3)]);
        store.Create(context.Job,declaration,"photographer",true);
        var first=JsonSerializer.SerializeToElement(store.Receive(context.Job,id,File([1,2,3]),"photographer"));
        var second=JsonSerializer.SerializeToElement(store.Receive(context.Job,id,File([1,2,3]),"photographer"));
        Assert.Equal(first.GetProperty("id").GetGuid(),second.GetProperty("id").GetGuid());
        Assert.True(second.GetProperty("reused").GetBoolean());
        Assert.Throws<ArgumentException>(()=>store.Receive(context.Job,id,File([3,2,1]),"photographer"));
        Assert.Throws<UnauthorizedAccessException>(()=>store.Receive(context.Job,id,File([1,2,3]),"other-photographer"));
        Assert.Throws<ArgumentException>(()=>store.Create(context.Job,declaration with{Files=[new(id,"changed.jpg",3)]},"photographer",true));
        using var count=new NpgsqlCommand("select count(*) from studio_asset_versions where job_id=$1",context.Connection);count.Parameters.AddWithValue(context.Job);Assert.Equal(1L,count.ExecuteScalar());
        // Closing a Job after storage succeeds must not turn a lost receipt into a new upload.
        using var close=new NpgsqlCommand("update studio_jobs set status='Completed' where id=$1",context.Connection);close.Parameters.AddWithValue(context.Job);close.ExecuteNonQuery();
        var replay=JsonSerializer.SerializeToElement(store.Receive(context.Job,id,File([1,2,3]),"photographer"));
        Assert.Equal(first.GetProperty("id").GetGuid(),replay.GetProperty("id").GetGuid());
    }
    [StudioPostgresFact]
    public void ExplicitUnclassifiedDestinationDoesNotUseTheJobDefaultCollection()
    {
        using var context=new IntegratedWorkflowTests.WorkflowContext();
        using(var schema=new NpgsqlCommand("create table studio_workflow_sessions(id uuid primary key,job_id uuid,required boolean);create table studio_workflow_crew(session_id uuid,user_name text);",context.Connection))schema.ExecuteNonQuery();
        var media=new MediaProductionStore(context.Database,context.Versions);
        media.Sync(context.Job);
        media.CreateCollection(context.Job,"Ceremony","coordinator");
        media.SetDefaultCollection(context.Job,"Ceremony","coordinator");
        var receipts=new MediaUploadReceipts(context.Database,media);
        var upload=Guid.NewGuid();
        receipts.Create(context.Job,new(Guid.NewGuid(),null,[new(upload,"photo.jpg",3)],"Unclassified"),"photographer",true);
        receipts.Receive(context.Job,upload,File([1,2,3]),"photographer");
        var asset=media.Snapshot(context.Job).GetProperty("assets")[0];
        Assert.Equal("Unclassified",asset.GetProperty("collection").GetString());
        // A receipt replay must keep the same immutable original.
        receipts.Receive(context.Job,upload,File([1,2,3]),"photographer");
        using var count=new NpgsqlCommand("select count(*) from studio_asset_versions where job_id=$1",context.Connection);
        count.Parameters.AddWithValue(context.Job);Assert.Equal(1L,count.ExecuteScalar());
    }
    private static IFormFile File(byte[] bytes)=>new FormFile(new MemoryStream(bytes),0,bytes.Length,"file","photo.jpg"){Headers=new HeaderDictionary(),ContentType="image/jpeg"};
}
