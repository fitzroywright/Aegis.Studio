using System.Text.Json;
using Common.Storage;

namespace Aegis.Studio.Web.Galleries;

public sealed record ClientGalleryPublication(string Reference,string Provider);
public sealed record ClientGalleryMedia(string AssetKey,Guid VersionId,string FileName,string StorageKey,string ContentType);
public sealed record ClientGalleryPublishRequest(Guid JobId,string Title,IReadOnlyList<ClientGalleryMedia> Media);
public interface IClientGalleryPublisher
{
    Task<ClientGalleryPublication> PublishAsync(ClientGalleryPublishRequest request,CancellationToken token);
}

// A real handoff manifest in the configured Common.Storage target. The consuming site
// is a separate integration; writing this manifest does not claim that a site is live.
public sealed class ManifestClientGalleryPublisher(IFileStorage storage):IClientGalleryPublisher
{
    public async Task<ClientGalleryPublication> PublishAsync(ClientGalleryPublishRequest request,CancellationToken token)
    {
        if(request.Media.Count==0)throw new ArgumentException("A gallery requires approved media.");
        string key=$"studio/client-galleries/{request.JobId:N}/publication.json";
        await using var content=new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new{schemaVersion=1,request.JobId,request.Title,request.Media},new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        await storage.StoreAsync(new StorageWriteRequest(key,content,"application/json","publication.json","Studio",new Dictionary<string,string>{{"jobId",request.JobId.ToString("D")}}),token);
        return new(key,"Manifest");
    }
}
