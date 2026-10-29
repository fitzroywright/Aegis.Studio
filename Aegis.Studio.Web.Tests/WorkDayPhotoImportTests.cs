using System.Security.Claims;
using Aegis.Studio.Web.Media;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Storage;
using Aegis.Studio.Web.WorkDay;
using Common.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Npgsql;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class WorkDayPhotoImportTests
{
    private static readonly byte[] Photo=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j9hQAAAAASUVORK5CYII=");
    private static ClaimsPrincipal User(string name)=>new(new ClaimsIdentity(
        [new Claim("login_name",name),new Claim(ClaimTypes.NameIdentifier,name),new Claim(ClaimTypes.Name,name)],"WorkDay"));
    private static IFormFile File(byte[]? bytes=null)
    {
        bytes??=Photo;
        return new FormFile(new MemoryStream(bytes),0,bytes.Length,"file","portrait.png")
            {Headers=new HeaderDictionary(),ContentType="image/png"};
    }

    [StudioPostgresFact]
    public async Task PhotoAndInternalJobMessageAreStoredOnceAndPartialCompletionCanResume()
    {
        using var f=new Fixture();Guid upload=Guid.NewGuid();
        var input=(FormFile)File();input.ContentType="text/html";
        WorkDayUploadResult first=await f.Import(upload,file:input);
        Assert.NotNull(first.AssetId);Assert.NotNull(first.VersionId);Assert.NotNull(first.MessageId);
        var media=new MediaProductionStore(f.Context.Database,f.Context.Versions).Snapshot(f.Job.Id);
        Assert.Contains(media.GetProperty("assets").EnumerateArray(),x=>x.GetProperty("id").GetGuid()==first.AssetId);
        Assert.Equal(1L,f.Count("studio_messages"));Assert.Equal(1L,f.Count("studio_attachments"));Assert.Equal(1L,f.Count("studio_asset_versions"));
        using(var type=new NpgsqlCommand("select content_type from studio_asset_versions",f.Context.Connection))Assert.Equal("image/png",type.ExecuteScalar());
        using(var q=new NpgsqlCommand("select body,visibility,author_user_name from studio_messages where id=$1",f.Context.Connection))
        {
            q.Parameters.AddWithValue(first.MessageId!.Value);using var r=q.ExecuteReader();Assert.True(r.Read());
            Assert.Contains("A photo was uploaded to this Job: portrait.png",r.GetString(0));
            Assert.Contains("photographer.one",r.GetString(0));Assert.Equal("Internal",r.GetString(1));Assert.Equal("photographer.one",r.GetString(2));
        }
        WorkDayUploadResult retry=await f.Import(upload);
        Assert.Equal(first.AssetId,retry.AssetId);Assert.Equal(first.VersionId,retry.VersionId);
        Assert.Equal(first.AttachmentId,retry.AttachmentId);Assert.Equal(first.MessageId,retry.MessageId);
        Assert.Equal(1L,f.Count("studio_messages"));Assert.Equal(1L,f.Count("studio_attachments"));Assert.Equal(1L,f.Count("studio_asset_versions"));
        // Simulate a failure after durable photo storage but before the message was retained.
        using(var remove=new NpgsqlCommand("delete from studio_messages where id=$1",f.Context.Connection))
        {remove.Parameters.AddWithValue(first.MessageId.Value);remove.ExecuteNonQuery();}
        await f.Import(upload);
        Assert.Equal(1L,f.Count("studio_messages"));Assert.Equal(1L,f.Count("studio_asset_versions"));
    }

    [StudioPostgresFact]
    public async Task ConcurrentRetriesCreateOneGalleryPhotoAttachmentAndMessage()
    {
        using var f=new Fixture();Guid upload=Guid.NewGuid();
        var results=await Task.WhenAll(Task.Run(()=>f.Import(upload)),Task.Run(()=>f.Import(upload)));
        Assert.Equal(results[0].AssetId,results[1].AssetId);Assert.Equal(results[0].MessageId,results[1].MessageId);
        Assert.Equal(1L,f.Count("studio_messages"));Assert.Equal(1L,f.Count("studio_attachments"));Assert.Equal(1L,f.Count("studio_asset_versions"));
    }

    [StudioPostgresFact]
    public async Task QuarantinedLegacyAttachmentIsNotPromotedToThePhotoGallery()
    {
        using var f=new Fixture();Guid upload=Guid.NewGuid();
        var legacy=await f.Attachments.AddAsync(f.Job.ConversationId,File(),"Internal","photographer.one",CancellationToken.None);
        using(var q=new NpgsqlCommand("update studio_attachments set storage_status='Quarantined' where id=$1",f.Context.Connection))
        {q.Parameters.AddWithValue(legacy.Id);q.ExecuteNonQuery();}
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Import(upload));
        Assert.Equal(0L,f.Count("studio_asset_versions"));Assert.Equal(0L,f.Count("studio_messages"));
    }

    [StudioPostgresFact]
    public async Task PreviouslyAcknowledgedAttachmentIsReusedWhileGalleryAndMessageAreRecovered()
    {
        using var f=new Fixture();Guid upload=Guid.NewGuid();
        StudioAttachment legacy=await f.Attachments.AddAsync(f.Job.ConversationId,File(),"Internal","photographer.one",CancellationToken.None,$"workday:{upload:D}");
        Assert.Equal(0L,f.Count("studio_asset_versions"));Assert.Equal(0L,f.Count("studio_messages"));
        WorkDayUploadResult repaired=await f.Import(upload);
        Assert.Equal(legacy.Id,repaired.AttachmentId);Assert.NotNull(repaired.AssetId);Assert.NotNull(repaired.MessageId);
        Assert.Equal(1L,f.Count("studio_attachments"));Assert.Equal(1L,f.Count("studio_asset_versions"));Assert.Equal(1L,f.Count("studio_messages"));
    }

    [StudioPostgresFact]
    public async Task ChangedBytesReporterOrAssignmentCannotReuseAnUploadIdentity()
    {
        using var f=new Fixture();Guid upload=Guid.NewGuid();await f.Import(upload);
        byte[] different=(byte[])Photo.Clone();different[^1]^=1;
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Import(upload,file:File(different)));
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Import(upload,actor:"photographer.two"));
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Import(upload,assignment:Guid.NewGuid()));
        Assert.Equal(1L,f.Count("studio_messages"));Assert.Equal(1L,f.Count("studio_asset_versions"));
    }

    [StudioPostgresFact]
    public async Task NonPhotoNativeUploadsRemainAttachmentsWithAnInternalMessage()
    {
        using var f=new Fixture();byte[] bytes=System.Text.Encoding.UTF8.GetBytes("Session notes");
        IFormFile file=new FormFile(new MemoryStream(bytes),0,bytes.Length,"file","notes.txt")
            {Headers=new HeaderDictionary(),ContentType="text/plain"};
        WorkDayUploadResult result=await f.Import(Guid.NewGuid(),file:file);
        Assert.Null(result.AssetId);Assert.Null(result.VersionId);Assert.NotNull(result.MessageId);
        Assert.Equal(1L,f.Count("studio_messages"));Assert.Equal(1L,f.Count("studio_attachments"));Assert.Equal(0L,f.Count("studio_asset_versions"));
    }

    [StudioPostgresFact]
    public async Task MissingRequiredSessionDoesNotCreatePhotoOrSuccessMessage()
    {
        using var f=new Fixture();
        using(var insert=new NpgsqlCommand("insert into studio_workflow_sessions(id,job_id,required,title) values($1,$2,true,'Required shoot')",f.Context.Connection))
        {insert.Parameters.AddWithValue(Guid.NewGuid());insert.Parameters.AddWithValue(f.Job.Id);insert.ExecuteNonQuery();}
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Import(Guid.NewGuid()));
        Assert.Equal(0L,f.Count("studio_messages"));Assert.Equal(0L,f.Count("studio_asset_versions"));Assert.Equal(0L,f.Count("studio_attachments"));
    }

    [StudioPostgresFact]
    public void DirectStudioImportRejectsInvalidRawBeforeCreatingVersion()
    {
        using var f=new Fixture();var media=new MediaProductionStore(f.Context.Database,f.Context.Versions);
        byte[] bytes=System.Text.Encoding.UTF8.GetBytes("not a valid RAW container");
        var file=new FormFile(new MemoryStream(bytes),0,bytes.Length,"file","capture.nef"){Headers=new HeaderDictionary(),ContentType="image/x-nikon-nef"};
        Assert.Throws<ArgumentException>(()=>media.Ingest(f.Job.Id,file,false,null,"photographer.one",null));
        Assert.Equal(0L,f.Count("studio_asset_versions"));
    }

    [StudioPostgresFact]
    public async Task RawOriginalIsDurableBeforeReceiptAndWrongChecksumCannotImport()
    {
        using var f=new Fixture();byte[] raw=new byte[256];
        using(var writer=new BinaryWriter(new MemoryStream(raw)))
        {
            writer.Write((byte)'I');writer.Write((byte)'I');writer.Write((ushort)42);writer.Write(8u);writer.Write((ushort)6);
            foreach(var entry in new (ushort Tag,ushort Type,uint Count,uint Value)[]{(256,4,1,1),(257,4,1,1),(262,3,1,32803),(273,4,1,200),(279,4,1,1),(50706,1,4,0x401)})
            {writer.Write(entry.Tag);writer.Write(entry.Type);writer.Write(entry.Count);writer.Write(entry.Value);}
        }
        raw[200]=99;
        IFormFile Input()=>new FormFile(new MemoryStream(raw),0,raw.Length,"file","capture.dng"){Headers=new HeaderDictionary(),ContentType="text/html"};
        string hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(raw));Guid id=Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidDataException>(()=>f.Import(id,file:Input(),sha256:new string('0',64)));
        Assert.Equal(0L,f.Count("studio_asset_versions"));Assert.Equal(0L,f.Count("studio_attachments"));
        var result=await f.Import(id,file:Input(),sha256:hash);
        Assert.Equal(hash,result.Sha256);Assert.NotNull(result.VersionId);Assert.Equal("Stored",result.StorageStatus);
        Assert.Equal(1L,f.Count("studio_asset_versions"));Assert.Equal(1L,f.Count("studio_attachments"));Assert.Equal(1L,f.Count("studio_messages"));
        var retry=await f.Import(id,file:Input(),sha256:hash);Assert.Equal(result.VersionId,retry.VersionId);Assert.Equal(result.AttachmentId,retry.AttachmentId);
    }

    [StudioPostgresFact]
    public async Task TwoHundredFiftyMiBRawImportsByteExactlyWithoutMemoryBuffering()
    {
        using var f=new Fixture();string path=Path.GetTempFileName();
        try
        {
            await using var source=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.Read);
            source.SetLength(250L*1024*1024);
            using(var writer=new BinaryWriter(source,System.Text.Encoding.UTF8,true))
            {
                writer.Write((byte)'I');writer.Write((byte)'I');writer.Write((ushort)42);writer.Write(8u);writer.Write((ushort)6);
                foreach(var e in new (ushort Tag,ushort Type,uint Count,uint Value)[]{(256,4,1,1),(257,4,1,1),(262,3,1,32803),(273,4,1,200),(279,4,1,1),(50706,1,4,0x401)})
                {writer.Write(e.Tag);writer.Write(e.Type);writer.Write(e.Count);writer.Write(e.Value);}
                source.Position=200;writer.Write((byte)99);writer.Flush();
            }
            source.Position=0;string hash=Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(source));source.Position=0;
            var file=new FormFile(source,0,source.Length,"file","large.dng"){Headers=new HeaderDictionary(),ContentType="application/octet-stream"};
            var result=await f.Import(Guid.NewGuid(),file:file,sha256:hash);Assert.Equal(source.Length,result.SizeBytes);Assert.NotNull(result.VersionId);
            var attachment=f.Attachments.GetForConversation(f.Job.ConversationId).Single(x=>x.Id==result.AttachmentId);
            await using var stored=await f.Attachments.OpenReadAsync(attachment,CancellationToken.None);
            Assert.Equal(hash,Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stored)));
            await using var version=f.Context.Versions.Open(f.Job.Id,result.VersionId!.Value,out _);
            Assert.Equal(hash,Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(version)));
        }
        finally{System.IO.File.Delete(path);}
    }

    private sealed class Fixture : IDisposable
    {
        public IntegratedWorkflowTests.WorkflowContext Context {get;}=new();
        public IFileStorage Storage {get;}
        public AttachmentStore Attachments {get;}
        public StudioStore Studio {get;}
        public StudioJob Job {get;}
        private readonly Guid assignment=Guid.NewGuid();
        public Fixture()
        {
            using(var setup=new NpgsqlCommand("""
                create table if not exists studio_workflow_sessions(id uuid primary key,job_id uuid,required boolean,shoot_id uuid);
                alter table studio_workflow_sessions add column if not exists shoot_id uuid;
                create table if not exists studio_workflow_crew(session_id uuid,user_name text);
                alter table studio_messages add column if not exists external_message_id text;
                alter table studio_messages add column if not exists author_user_name text;
                alter table studio_conversations add column if not exists external_thread_id text;
                alter table studio_attachments add column if not exists source_key text;
                """,Context.Connection))setup.ExecuteNonQuery();
            Storage=new LocalFileStorage(Context.Root);
            Attachments=new AttachmentStore(new TestEnvironment(Context.Root),new ConfigurationBuilder().Build(),Context.Database,Storage);
            Studio=new StudioStore(Context.Database,new WorkspaceStore(Context.Database));
            using var q=new NpgsqlCommand("select conversation_id from studio_jobs where id=$1",Context.Connection);q.Parameters.AddWithValue(Context.Job);
            Job=new StudioJob(Context.Job,Context.Workspace,(Guid)q.ExecuteScalar()!,"Test Job","Client","In Production",null,DateTimeOffset.UtcNow);
        }
        public Task<WorkDayUploadResult> Import(Guid upload,string actor="photographer.one",Guid? assignment=null,IFormFile? file=null,string? sha256=null)
            =>WorkDayPhotoImport.ImportAsync(Context.Database,Storage,Attachments,Studio,Job,assignment??this.assignment,upload,file??File(),User(actor),CancellationToken.None,expectedSha256:sha256);
        public long Count(string table)
        {using var q=new NpgsqlCommand("select count(*) from "+table,Context.Connection);return (long)q.ExecuteScalar()!;}
        public void Dispose()=>Context.Dispose();
    }
    private sealed class TestEnvironment(string root):IWebHostEnvironment
    {
        public string ApplicationName{get;set;}="Tests";
        public string EnvironmentName{get;set;}="Test";
        public string ContentRootPath{get;set;}=root;
        public string WebRootPath{get;set;}=root;
        public IFileProvider ContentRootFileProvider{get;set;}=new NullFileProvider();
        public IFileProvider WebRootFileProvider{get;set;}=new NullFileProvider();
    }
}
