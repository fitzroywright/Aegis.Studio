using System.Security.Cryptography;
using Aegis.Studio.Web.Storage;
using Aegis.Studio.Web.Versioning;
using Common.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class StorageRecoveryTests
{
    [Fact]
    public async Task InterruptedCurrentObjectRecoversFromMatchingProviderVersion()
    {
        string root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
        try
        {
            byte[] bytes=[1,2,3,4];var version=Version("job/original.jpg",bytes);var storage=new LocalFileStorage(root);
            await storage.StoreAsync(new("VersionStore/"+version.StoredPath,new MemoryStream(bytes),version.ContentType,version.FileName,version.CreatedBy));
            File.Delete(Path.Combine(root,"VersionStore",version.StoredPath));
            using var file=await new StoredVersionRecovery(storage,new TestEnvironment(root)).OpenAsync(version);
            using var output=new MemoryStream();await file.CopyToAsync(output);Assert.Equal(bytes,output.ToArray());
            Assert.True(File.Exists(Path.Combine(root,"VersionStore",version.StoredPath)));
        }
        finally {if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Theory]
    [InlineData("VersionStore")]
    [InlineData("Storage/VersionStore")]
    public async Task MissingVersionRecoversIdenticalLegacyBytesIntoConfiguredStorage(string legacyRoot)
    {
        string root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
        try
        {
            byte[] bytes=[1,2,3,4];var version=Version("job/original.jpg",bytes);
            string legacy=Path.Combine(root,legacyRoot,version.StoredPath);Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);await File.WriteAllBytesAsync(legacy,bytes);
            var storage=new LocalFileStorage(Path.Combine(root,"current"));
            using var file=await new StoredVersionRecovery(storage,new TestEnvironment(root)).OpenAsync(version);
            using var output=new MemoryStream();await file.CopyToAsync(output);Assert.Equal(bytes,output.ToArray());
            Assert.NotNull(await storage.GetMetadataAsync("VersionStore/"+version.StoredPath));Assert.True(File.Exists(legacy));
        }
        finally {if(Directory.Exists(root))Directory.Delete(root,true);}
    }

    [Theory]
    [InlineData("job/original.jpg")]
    [InlineData("../outside.jpg")]
    public async Task MissingOriginalIsNeverReplacedWithDifferentBytesOrAnOutsideFile(string key)
    {
        string root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
        try
        {
            byte[] original=[1,2,3,4];var version=Version(key,original);string legacy=Path.GetFullPath(Path.Combine(root,"VersionStore",key));Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
            await File.WriteAllBytesAsync(legacy,key.Contains("..")?original:[4,3,2,1]);
            var storage=new LocalFileStorage(Path.Combine(root,"current"));
            var error=await Assert.ThrowsAsync<StorageException>(()=>new StoredVersionRecovery(storage,new TestEnvironment(root)).OpenAsync(version));
            Assert.NotNull(error);Assert.Empty(Directory.EnumerateFiles(Path.Combine(root,"current"),"*",SearchOption.AllDirectories));
        }
        finally {if(Directory.Exists(root))Directory.Delete(root,true);}
    }

    [Theory]
    [InlineData("STORAGE-MISSING-001",404)]
    [InlineData("STORAGE-INTEGRITY-001",409)]
    [InlineData("STORAGE-NETWORK-001",503)]
    public async Task StorageFailureReturnsRecoverableProblemAndNextRequestStillSucceeds(string code,int status)
    {
        using var services=new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var middleware=new StorageFailureMiddleware(context=>context.Request.Path=="/api/missing"?throw new StorageException(code,"Private physical path should stay in logs."):Task.CompletedTask,NullLogger<StorageFailureMiddleware>.Instance);
        var context=new DefaultHttpContext {RequestServices=services};context.Request.Path="/api/missing";context.Response.Body=new MemoryStream();
        await middleware.InvokeAsync(context);Assert.Equal(status,context.Response.StatusCode);context.Response.Body.Position=0;
        string body=await new StreamReader(context.Response.Body).ReadToEndAsync();Assert.Contains(code,body);Assert.DoesNotContain("Private physical path",body);
        var next=new DefaultHttpContext();next.Request.Path="/api/healthy";await middleware.InvokeAsync(next);Assert.Equal(200,next.Response.StatusCode);
    }

    private static StudioAssetVersion Version(string key,byte[] bytes)=>new(Guid.NewGuid(),Guid.NewGuid(),"original",1,"original.jpg","image/jpeg",bytes.Length,Convert.ToHexString(SHA256.HashData(bytes)),key,"staff",DateTimeOffset.UtcNow,null,null);
    private sealed class TestEnvironment(string root):IWebHostEnvironment
    {
        public string ApplicationName {get;set;}="Tests";public string EnvironmentName {get;set;}="Development";public string ContentRootPath {get;set;}=root;public string WebRootPath {get;set;}=root;
        public IFileProvider ContentRootFileProvider {get;set;}=new NullFileProvider();public IFileProvider WebRootFileProvider {get;set;}=new NullFileProvider();
    }
}
