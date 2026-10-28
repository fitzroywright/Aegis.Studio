using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Aegis.Studio.Web.WorkDay;
using Aegis.Studio.WorkDay.Core;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class RawPhotoTransferTests
{
    [Fact]
    public void DecoderEnvironmentExcludesApplicationCredentialsAndRuntimeInjection()
    {
        var start=new System.Diagnostics.ProcessStartInfo("python3");
        foreach(string name in new[]{"SESSION_SECRET","WorkDay__ApiSyncKey","GOOGLE_CLIENT_SECRET","PYTHONPATH"})start.Environment[name]="synthetic-test-value";
        Aegis.Studio.Web.Media.PhotoProcessEnvironment.Configure(start);
        foreach(string name in new[]{"SESSION_SECRET","WorkDay__ApiSyncKey","GOOGLE_CLIENT_SECRET","PYTHONPATH"})Assert.False(start.Environment.ContainsKey(name));
        Assert.Equal("1",start.Environment["OMP_NUM_THREADS"]);
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    [Fact]
    public async Task MobileTransferResumesConfirmedChunksAfterDisconnectAndVerifiesCompletion()
    {
        string path=Path.GetTempFileName();byte[] bytes=new byte[5*1024*1024];Random.Shared.NextBytes(bytes);
        await System.IO.File.WriteAllBytesAsync(path,bytes);
        try
        {
            var handler=new TransferHandler(bytes);using var http=new HttpClient(handler){BaseAddress=new Uri("https://synthetic.invalid/api/v1/")};
            var file=new PhotoTransfer(Guid.NewGuid(),Guid.NewGuid(),path,"capture.nef",bytes.Length);
            await Assert.ThrowsAsync<HttpRequestException>(()=>ResumablePhotoTransfer.SendAsync(http,file,null,CancellationToken.None));
            Assert.Equal(new[]{0,1},handler.Attempted);
            Assert.Equal(Hash(bytes),await ResumablePhotoTransfer.SendAsync(http,file,null,CancellationToken.None));
            Assert.Equal(new[]{0,1,1},handler.Attempted);Assert.Equal(2,handler.Chunks.Count);
            handler.WrongReceipt=true;
            await Assert.ThrowsAsync<InvalidOperationException>(()=>ResumablePhotoTransfer.SendAsync(http,file,null,CancellationToken.None));
            Assert.True(System.IO.File.Exists(path));Assert.Equal(Hash(bytes),Hash(await System.IO.File.ReadAllBytesAsync(path)));
        }
        finally{System.IO.File.Delete(path);}
    }
    [Fact]
    public async Task MobileRejectsChangedChunkAndCancellationWithoutDeletingLocalOriginal()
    {
        string path=Path.GetTempFileName();byte[] bytes=[1,2,3];await System.IO.File.WriteAllBytesAsync(path,bytes);
        try
        {
            var handler=new TransferHandler(bytes);handler.Chunks[0]="0".PadLeft(64,'0');
            using var http=new HttpClient(handler){BaseAddress=new Uri("https://synthetic.invalid/api/v1/")};
            var file=new PhotoTransfer(Guid.NewGuid(),Guid.NewGuid(),path,"capture.dng",bytes.Length);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>ResumablePhotoTransfer.SendAsync(http,file,null,CancellationToken.None));
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>ResumablePhotoTransfer.SendAsync(http,file,null,cancelled.Token));
            Assert.Empty(handler.Attempted);Assert.True(System.IO.File.Exists(path));
        }
        finally{System.IO.File.Delete(path);}
    }
    [Theory]
    [InlineData(false,false)] [InlineData(true,false)] [InlineData(false,true)]
    public async Task StudioPrivatePullChecksIdentityHeadersAndStreamedBytes(bool wrongBytes,bool truncated)
    {
        byte[] original=new byte[1024*1024];Random.Shared.NextBytes(original);byte[] body=(byte[])original.Clone();
        if(wrongBytes)body[10]^=1;if(truncated)body=body[..^1];Guid upload=Guid.NewGuid();
        using var http=new HttpClient(new PullHandler(upload,body,original.Length,Hash(original))){BaseAddress=new Uri("https://synthetic.invalid/api/v1/")};
        if(wrongBytes||truncated)
            await Assert.ThrowsAsync<InvalidDataException>(()=>WorkDayOriginalDownload.FetchAsync(http,upload,original.Length,Hash(original),CancellationToken.None));
        else
        {
            await using var result=await WorkDayOriginalDownload.FetchAsync(http,upload,original.Length,Hash(original),CancellationToken.None);
            Assert.IsType<FileStream>(result);Assert.Equal(original.Length,result.Length);
            Assert.Equal(Hash(original),Convert.ToHexString(await SHA256.HashDataAsync(result)).ToLowerInvariant());
        }
    }
    private sealed class PullHandler(Guid upload,byte[] body,long length,string hash):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Assert.Equal($"/api/v1/admin/workday/uploads/{upload:D}/original",request.RequestUri!.AbsolutePath);
            var response=new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(new MemoryStream(body))};
            response.Content.Headers.ContentLength=length;response.Headers.Add("X-Content-SHA256",hash);return Task.FromResult(response);
        }
    }
    private sealed class TransferHandler(byte[] original):HttpMessageHandler
    {
        public Dictionary<int,string> Chunks{get;}=[];public List<int> Attempted{get;}=[];public bool WrongReceipt;
        private bool disconnected;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var path=request.RequestUri!.AbsolutePath;
            if(path.EndsWith("/upload-sessions"))return Json(new{state="Receiving",chunkBytes=4*1024*1024,receivedChunks=Chunks.Select(x=>new{chunkIndex=x.Key,sha256=x.Value})});
            if(request.Method==HttpMethod.Put)
            {
                int index=int.Parse(path.Split('/')[^1]);Attempted.Add(index);
                if(index==1&&!disconnected){disconnected=true;throw new HttpRequestException("Synthetic interruption");}
                byte[] bytes=await request.Content!.ReadAsByteArrayAsync(ct);Assert.InRange(bytes.Length,1,4*1024*1024);
                Assert.Equal(Hash(bytes),request.Headers.GetValues("X-Chunk-SHA256").Single());Chunks[index]=Hash(bytes);return Json(new{});
            }
            Assert.EndsWith("/complete",path);return Json(new{sha256=WrongReceipt?new string('0',64):Hash(original)});
        }
        private static HttpResponseMessage Json<T>(T value)=>new(HttpStatusCode.OK){Content=JsonContent.Create(value)};
    }
}
