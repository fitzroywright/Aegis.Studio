using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Aegis.Studio.Web.Media;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Production;
using Aegis.Studio.Web.Scheduling;
using Aegis.Studio.Web.Storage;
using Aegis.Studio.Web.WorkDay;
using Aegis.Studio.WorkDay.Core;
using Common.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class WorkDayConnectedUploadTests
{
    [StudioPostgresFact]
    public async Task LargeWorkDayUploadImportsThroughRealWorkerReplaysLostAckAndPurgesOnlyAfterGrace()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(4));var ct=deadline.Token;
        using var context=new IntegratedWorkflowTests.WorkflowContext();var db=context.Database;
        const string user="connected.photographer";
        var config=new ConfigurationBuilder().Build();var storage=new LocalFileStorage(context.Root);
        var photographer=new PhotographerStore(db).Save(new(user,"Connected local photographer","Employee",true,[],null));
        var schedules=new ScheduleStore(db,config);var start=DateTimeOffset.UtcNow.AddHours(1);
        var assignment=schedules.Create(new(context.Job,null,"Connected local acceptance",start,start.AddHours(1),"Disposable studio","Scheduled",null,[photographer.ScheduleResourceId]));
        new OperationalWorkStore(db).CreateShoot(context.Job,new(assignment.Id,user,null,start,start.AddHours(1),"Disposable studio",null,null),"Local test");
        var access=new JobAccessService(db);
        access.SaveMember(context.Job,new(user,"Connected local photographer",true,false,JobPermissionSet.Viewer with{AddAttachments=true}));
        Guid session=WorkDayPhotoImport.SessionForAssignment(db,context.Job,assignment.Id,Guid.Empty)!.Value;
        using var services=new ServiceCollection().AddLogging().AddSingleton(db).AddSingleton<IConfiguration>(config)
            .AddSingleton<IFileStorage>(storage).AddSingleton<IWebHostEnvironment>(new TestEnvironment(context.Root))
            .AddSingleton<WorkspaceStore>().AddSingleton<StudioStore>().AddSingleton<ScheduleStore>()
            .AddSingleton<JobAccessService>().AddSingleton<AttachmentStore>().AddSingleton<StudioOperationsFlowPublisher>().BuildServiceProvider();
        using var worker=new WorkDayApiExchangeWorker(services.GetRequiredService<IServiceScopeFactory>(),config,NullLogger<WorkDayApiExchangeWorker>.Instance);
        await using var site=await LocalSite.StartAsync(context.Root,ct);
        var failures=new LostAckHandler();using var admin=new HttpClient(failures){BaseAddress=site.BaseUrl};
        using(var request=new HttpRequestMessage(HttpMethod.Post,"admin/integration-keys"))
        {
            request.Headers.Add("X-Site-Provisioning-Token",LocalSite.ProvisioningToken);
            request.Content=JsonContent.Create(new{name="Disposable connected acceptance",scopes=new[]{"workday:sync"}});
            using var response=await admin.SendAsync(request,ct);response.EnsureSuccessStatusCode();
            var key=await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            admin.DefaultRequestHeaders.Authorization=new("Bearer",key.GetProperty("secret").GetString());
        }
        var provision=await Post(admin,"admin/workday/users",new{userName=user,displayName="Connected local photographer"},ct);
        using var employee=new HttpClient(new HttpClientHandler{CookieContainer=new CookieContainer()}){BaseAddress=site.BaseUrl};
        string password=Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        await Post(employee,"workday/enroll",new{enrollmentToken=provision.GetProperty("enrollmentToken").GetString(),password},ct);
        await Post(employee,"auth/login",new{userName=user,password},ct);
        using(var response=await admin.PutAsJsonAsync("admin/workday/snapshots",new{userName=user,revision=1,snapshot=new{today=new[]{new{id=assignment.Id,jobId=context.Job,sessionId=session,title="Connected local acceptance"}},upcoming=Array.Empty<object>()}},ct))response.EnsureSuccessStatusCode();
        string original=Path.Combine(context.Root,"connected-original.dng");await CreateLargeDng(original,ct);
        Guid upload=Guid.NewGuid();var transfer=new PhotoTransfer(upload,assignment.Id,original,"connected-original.dng",250L*1024*1024);
        string hash=await ResumablePhotoTransfer.SendAsync(employee,transfer,null,ct);
        string originalUrl=$"admin/workday/uploads/{upload:D}/original",operation=$"workday/operations/{upload:D}";
        using(var denied=await employee.GetAsync(originalUrl,HttpCompletionOption.ResponseHeadersRead,ct))Assert.Equal(HttpStatusCode.Unauthorized,denied.StatusCode);
        Assert.Equal(0,(await Post(admin,"admin/workday/upload-retention",new{},ct)).GetProperty("purged").GetInt32());
        // The actual production pull/import method commits gallery/version/attachment/message, then loses its ACK.
        failures.DropNextAcknowledgment=true;
        await Assert.ThrowsAsync<HttpRequestException>(()=>worker.PullUploadsAsync(admin,ct));
        var status=await employee.GetFromJsonAsync<JsonElement>(operation,ct);Assert.Equal("Pending",status.GetProperty("state").GetString());
        Assert.Equal(0,(await Post(admin,"admin/workday/upload-retention",new{},ct)).GetProperty("purged").GetInt32());
        var gallery=new MediaProductionStore(db,context.Versions).Snapshot(context.Job);
        var asset=Assert.Single(gallery.GetProperty("assets").EnumerateArray());Guid versionId=asset.GetProperty("versionId").GetGuid();
        using(var retained=context.Versions.Open(context.Job,versionId,out var version))
        {Assert.Equal(transfer.SizeBytes,version.SizeBytes);Assert.Equal(hash,Convert.ToHexString(await SHA256.HashDataAsync(retained,ct)).ToLowerInvariant());}
        // Retry uses the same durable receipt; no duplicate image, attachment or message may be introduced.
        await worker.PullUploadsAsync(admin,ct);
        status=await employee.GetFromJsonAsync<JsonElement>(operation,ct);Assert.Equal("Acknowledged",status.GetProperty("state").GetString());
        var receipt=status.GetProperty("result").GetProperty("result");Assert.Equal(hash,receipt.GetProperty("sha256").GetString()!.ToLowerInvariant());
        Assert.Equal(versionId,receipt.GetProperty("versionId").GetGuid());Assert.Equal("Stored",receipt.GetProperty("storageStatus").GetString());
        Assert.Single(new MediaProductionStore(db,context.Versions).Snapshot(context.Job).GetProperty("assets").EnumerateArray());
        using(var count=new NpgsqlCommand("select (select count(*) from studio_attachments),(select count(*) from studio_messages where body like 'A photo was uploaded to this Job:%')",context.Connection))
        using(var row=count.ExecuteReader()){Assert.True(row.Read());Assert.Equal(1L,row.GetInt64(0));Assert.Equal(1L,row.GetInt64(1));}
        Assert.Equal(0,(await Post(admin,"admin/workday/upload-retention",new{},ct)).GetProperty("purged").GetInt32());
        using(var retained=await admin.GetAsync(originalUrl,HttpCompletionOption.ResponseHeadersRead,ct))Assert.Equal(HttpStatusCode.OK,retained.StatusCode);
        await Task.Delay(TimeSpan.FromMilliseconds(2200),ct);
        Assert.Equal(1,(await Post(admin,"admin/workday/upload-retention",new{},ct)).GetProperty("purged").GetInt32());
        using(var gone=await admin.GetAsync(originalUrl,HttpCompletionOption.ResponseHeadersRead,ct))Assert.Equal(HttpStatusCode.NotFound,gone.StatusCode);
        var webGallery=await employee.GetFromJsonAsync<JsonElement>($"workday/assignments/{assignment.Id:D}/uploads",ct);
        Assert.Equal("Acknowledged",Assert.Single(webGallery.GetProperty("items").EnumerateArray()).GetProperty("state").GetString());
        using var authoritative=context.Versions.Open(context.Job,versionId,out _);
        Assert.Equal(hash,Convert.ToHexString(await SHA256.HashDataAsync(authoritative,ct)).ToLowerInvariant());
    }
    private static async Task<JsonElement> Post(HttpClient http,string path,object body,CancellationToken ct)
    {using var response=await http.PostAsJsonAsync(path,body,ct);response.EnsureSuccessStatusCode();return await response.Content.ReadFromJsonAsync<JsonElement>(ct);}
    private static async Task CreateLargeDng(string path,CancellationToken ct)
    {
        await using var output=new FileStream(path,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.Read);output.SetLength(250L*1024*1024);
        using var writer=new BinaryWriter(output,System.Text.Encoding.UTF8,true);
        writer.Write((byte)'I');writer.Write((byte)'I');writer.Write((ushort)42);writer.Write(8u);writer.Write((ushort)6);
        foreach(var e in new (ushort Tag,ushort Type,uint Count,uint Value)[]{(256,4,1,1),(257,4,1,1),(262,3,1,32803),(273,4,1,200),(279,4,1,1),(50706,1,4,0x401)})
        {writer.Write(e.Tag);writer.Write(e.Type);writer.Write(e.Count);writer.Write(e.Value);}
        output.Position=200;writer.Write((byte)99);writer.Flush();await output.FlushAsync(ct);
    }
    private sealed class LostAckHandler:DelegatingHandler
    {
        public bool DropNextAcknowledgment;
        public LostAckHandler():base(new HttpClientHandler()){}
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            if(DropNextAcknowledgment&&request.Method==HttpMethod.Post&&request.RequestUri!.AbsolutePath.EndsWith("/result"))
            {DropNextAcknowledgment=false;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));}
            return base.SendAsync(request,ct);
        }
    }
    private sealed class LocalSite(Process process,Uri baseUrl):IAsyncDisposable
    {
        public const string ProvisioningToken="connected-test-only-provisioning-token-32-characters";
        public Uri BaseUrl{get;}=baseUrl;
        public static async Task<LocalSite> StartAsync(string root,CancellationToken ct)
        {
            var repository=new DirectoryInfo(AppContext.BaseDirectory);
            while(repository is not null&&!System.IO.File.Exists(Path.Combine(repository.FullName,"tools","workday-connected-fixture.mjs")))repository=repository.Parent;
            Assert.NotNull(repository);
            var start=new ProcessStartInfo("node"){UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
            start.ArgumentList.Add(Path.Combine(repository.FullName,"tools","workday-connected-fixture.mjs"));start.ArgumentList.Add(Path.Combine(root,"public-site"));
            Directory.CreateDirectory(Path.Combine(root,"public-site"));start.Environment["CONNECTED_TEST_PROVISIONING_TOKEN"]=ProvisioningToken;
            var process=Process.Start(start)!;
            try
            {
                _=process.StandardError.ReadToEndAsync(ct);string? line=await process.StandardOutput.ReadLineAsync(ct);
                Assert.NotNull(line);using var json=JsonDocument.Parse(line);return new(process,new Uri(json.RootElement.GetProperty("baseUrl").GetString()!));
            }
            catch{if(!process.HasExited)process.Kill(true);process.Dispose();throw;}
        }
        public async ValueTask DisposeAsync()
        {process.StandardInput.Close();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));try{await process.WaitForExitAsync(deadline.Token);}catch(OperationCanceledException){process.Kill(true);await process.WaitForExitAsync();}process.Dispose();}
    }
    private sealed class TestEnvironment(string root):IWebHostEnvironment
    {
        public string ApplicationName{get;set;}="Connected local acceptance";public string EnvironmentName{get;set;}="Test";
        public string ContentRootPath{get;set;}=root;public string WebRootPath{get;set;}=root;
        public IFileProvider ContentRootFileProvider{get;set;}=new NullFileProvider();public IFileProvider WebRootFileProvider{get;set;}=new NullFileProvider();
    }
}
