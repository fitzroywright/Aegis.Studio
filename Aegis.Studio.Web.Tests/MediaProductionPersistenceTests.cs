using System.IO.Compression;
using System.Text;
using Aegis.Studio.Web.FrontDesk;
using Aegis.Studio.Web.Media;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Versioning;
using Aegis.Studio.Web.WorkspaceJobs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Npgsql;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class MediaProductionPersistenceTests
{
    [StudioPostgresFact]
    public void RosterEditingRoundTripAndWeddingClassificationPreserveIdentity()
    {
        string connectionString=Environment.GetEnvironmentVariable("STUDIO_TEST_POSTGRES")!;
        string schema="studio_media_test_"+Guid.NewGuid().ToString("N"),root=Path.Combine(Path.GetTempPath(),schema);
        using var admin=new NpgsqlConnection(connectionString);admin.Open();Sql(admin,$"create schema {schema}");
        var settings=new NpgsqlConnectionStringBuilder(connectionString){SearchPath=schema};
        using var source=NpgsqlDataSource.Create(settings.ConnectionString);var db=new StudioDatabase(source);
        try
        {
            using(var c=db.OpenConnection())
            {
                Sql(c,"create table studio_workspaces(id uuid primary key)");Sql(c,"create table studio_jobs(id uuid primary key,workspace_id uuid,title text,assigned_to text,event_date date)");
                Sql(c,"create table studio_operational_audit(id uuid primary key,job_id uuid,action text,user_name text,detail text,created_at_utc timestamptz)");
            }
            new WorkspaceJobStore(db).EnsureSchema();var versions=new StudioVersionStore(db,new ConfigurationBuilder().Build(),new TestEnvironment(root));
            MediaProductionStore.Migrate(db);MediaProductionStore.Migrate(db);var store=new MediaProductionStore(db,versions);
            Guid job=Guid.NewGuid(),wedding=Guid.NewGuid();using(var c=db.OpenConnection()){Sql(c,"insert into studio_jobs(id,workspace_id,title) values($1,$2,'Employee IDs'),($3,$4,'Brown wedding')",job,Guid.NewGuid(),wedding,Guid.NewGuid());}
            string csv="Subject ID,Name,Department\n"+string.Join("\n",Enumerable.Range(1,50).Select(i=>$"{i},Employee {i},Operations"));
            Assert.Equal(50,store.ImportSubjects(job,new(csv,"Name","Subject ID"),"editor"));
            store.ImportSubjects(job,new(csv.Replace("Employee 1,","Updated Employee 1,"),"Name","Subject ID"),"editor");
            Assert.Equal(50,store.Snapshot(job).GetProperty("subjects").GetArrayLength());
            byte[] jpeg=[0xff,0xd8,0xff,0xda,0,2,12,34,0xff,0xd9];
            store.Ingest(job,File(jpeg,"original.jpg"),false,null,"editor",null);
            var snapshot=store.Snapshot(job);var asset=snapshot.GetProperty("assets")[0];Guid assetId=asset.GetProperty("id").GetGuid();string key=asset.GetProperty("assetKey").GetString()!;
            Guid subject=snapshot.GetProperty("subjects")[0].GetProperty("id").GetGuid();
            store.UpdateAsset(job,assetId,new("Needs Editing","ID portraits",[subject],"editor"),"editor");
            Assert.Equal(1,store.Snapshot(job).GetProperty("assets")[0].GetProperty("subjects").GetArrayLength());
            byte[] zip=store.Export(job,[assetId],"editor");using var archive=new ZipArchive(new MemoryStream(zip));
            Assert.NotNull(archive.GetEntry("studio-editing-manifest.json"));using var bytes=new MemoryStream();using(var file=archive.Entries.Single(e=>e.Name.EndsWith(".jpg")).Open())file.CopyTo(bytes);
            store.Ingest(job,File(bytes.ToArray(),"renamed-final.jpg"),true,null,"editor","Retouched");
            store.Ingest(job,File(bytes.ToArray(),"renamed-final.jpg"),true,null,"editor","Replay");
            Assert.Equal(2,versions.GetHistory(job,key).Count);Assert.Equal("renamed-final.jpg",versions.GetHistory(job,key)[0].FileName);
            store.Ingest(wedding,File(bytes.ToArray(),"wrong-job.jpg"),true,null,"editor",null);
            Assert.Empty(store.Snapshot(wedding).GetProperty("assets").EnumerateArray());Assert.Single(store.Snapshot(wedding).GetProperty("unmatched").EnumerateArray());
            store.Ingest(job,File(jpeg,"unrecognized.jpg"),true,null,"editor",null);
            var unmatched=store.Snapshot(job).GetProperty("unmatched")[0].GetProperty("id").GetGuid();store.Resolve(job,unmatched,assetId,"editor");Assert.Equal(3,versions.GetHistory(job,key).Count);
            byte[] stale=bytes.ToArray().Concat(new byte[]{0}).ToArray();store.Ingest(job,File(stale,"old-edit.jpg"),true,null,"editor",null);
            Assert.Equal(3,versions.GetHistory(job,key).Count);Assert.Single(store.Snapshot(job).GetProperty("unmatched").EnumerateArray());
            store.ImportSubjects(wedding,new("ID,Name\nbride,Bride\ngroom,Groom","Name","ID"),"editor");store.Ingest(wedding,File(jpeg,"ceremony.jpg"),false,null,"editor",null);
            var weddingData=store.Snapshot(wedding);Guid weddingAsset=weddingData.GetProperty("assets")[0].GetProperty("id").GetGuid();Guid[] weddingSubjects=weddingData.GetProperty("subjects").EnumerateArray().Select(x=>x.GetProperty("id").GetGuid()).ToArray();
            store.UpdateAsset(wedding,weddingAsset,new("Ready","Ceremony",weddingSubjects,null),"editor");Assert.Single(store.Snapshot(wedding).GetProperty("assets").EnumerateArray());Assert.Equal(2,store.Snapshot(wedding).GetProperty("assets")[0].GetProperty("subjects").GetArrayLength());
            var clients=new StudioClientStore(db);clients.Migrate();var client=clients.Save(null,new("Shared client","client@example.invalid",null));clients.Link(job,client.Id,"frontdesk");clients.Link(wedding,client.Id,"frontdesk");Assert.Single(clients.List());clients.Link(job,null,"frontdesk");
            using(var c=db.OpenConnection()){
                Sql(c,"alter table studio_workspaces add column is_enabled boolean default true");Sql(c,"insert into studio_workspaces(id) select workspace_id from studio_jobs");
                Sql(c,"alter table studio_jobs add column client_name text default 'Customer'");Sql(c,"alter table studio_jobs add column conversation_id uuid");
                Sql(c,"create table studio_conversations(id uuid primary key,contact_address text)");Sql(c,"insert into studio_conversations select id,'client@example.invalid' from studio_jobs");Sql(c,"update studio_jobs set conversation_id=id");
                Sql(c,"create table studio_notifications(id uuid primary key,user_name text,channel text,event_type text,subject text,body text,reference_type text,reference_id text,status text,destination text,attempts int,is_read boolean,created_at_utc timestamptz,last_error text)");
            }
            clients.Link(job,client.Id,"frontdesk");var accounts=new StudioAccountsStore(db);accounts.EnsureSchema();
            var estimate=new InvoiceRequest([new("Photography",1,500)],0,DateTimeOffset.UtcNow.AddDays(30));
            var invoice=accounts.CreateInvoice(job,estimate,"frontdesk",[wedding]);Assert.Equal(invoice.Id,accounts.GetInvoices(wedding).Single().Id);
            var other=clients.Save(null,new("Different customer",null,null));clients.Link(wedding,other.Id,"frontdesk");Assert.Throws<InvalidOperationException>(()=>accounts.CreateInvoice(job,estimate,"frontdesk",[wedding]));
            new FrontDeskBookingRequestStore(db).EnsureSchema();var quotes=new StudioQuoteStore(db);quotes.Migrate();Guid workspace;
            using(var c=db.OpenConnection()){using var command=new NpgsqlCommand("select workspace_id from studio_jobs where id=$1",c);command.Parameters.AddWithValue(job);workspace=(Guid)command.ExecuteScalar()!;}
            var quote=quotes.Create(new(client.Id,workspace,"ID photos",DateOnly.FromDateTime(DateTime.Today.AddDays(10)),"Bring employee roster",estimate,100),"frontdesk");
            Assert.Throws<ArgumentException>(()=>quotes.ConvertToBooking(quote,"frontdesk"));quotes.Send(quote,"frontdesk");Assert.Equal("Pending",quotes.List(client.Id)[0].GetProperty("deliveryStatus").GetString());
            Assert.Throws<ArgumentException>(()=>quotes.Send(quote,"frontdesk"));quotes.Accept(quote,"Customer email confirmation");Guid pending=quotes.ConvertToBooking(quote,"frontdesk");Assert.Equal(pending,quotes.ConvertToBooking(quote,"frontdesk"));Assert.Null(new FrontDeskBookingRequestStore(db).GetAll().Single().JobId);

        }
        finally {source.Dispose();Sql(admin,$"drop schema {schema} cascade");if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    private static IFormFile File(byte[] bytes,string name)=>new FormFile(new MemoryStream(bytes),0,bytes.Length,"file",name){Headers=new HeaderDictionary(),ContentType="image/jpeg"};
    private static void Sql(NpgsqlConnection c,string sql,params object[] values){using var cmd=new NpgsqlCommand(sql,c);foreach(var value in values)cmd.Parameters.AddWithValue(value);cmd.ExecuteNonQuery();}
    private sealed class TestEnvironment(string root):IWebHostEnvironment
    {
        public string ApplicationName {get;set;}="Tests";public string EnvironmentName {get;set;}="Development";public string ContentRootPath {get;set;}=root;public string WebRootPath {get;set;}=root;
        public IFileProvider ContentRootFileProvider {get;set;}=new NullFileProvider();public IFileProvider WebRootFileProvider {get;set;}=new NullFileProvider();
    }
}
