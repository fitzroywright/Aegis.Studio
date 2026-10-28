using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
namespace Aegis.Studio.WorkDay.Core;

public sealed record PhotoTransfer(Guid UploadId,Guid AssignmentId,string Path,string FileName,long SizeBytes);
public sealed record PhotoTransferProgress(long Bytes,long Total,string State);
public static class ResumablePhotoTransfer
{
    public const long MaximumBytes=512L*1024*1024;
    public static async Task<string> SendAsync(HttpClient http,PhotoTransfer file,IProgress<PhotoTransferProgress>? progress,CancellationToken ct)
    {
        await using var input=new FileStream(file.Path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous);
        if(input.Length!=file.SizeBytes||input.Length<1||input.Length>MaximumBytes)throw new InvalidOperationException("The retained original size changed or exceeds the upload limit.");
        progress?.Report(new(0,file.SizeBytes,"Hashing"));string checksum=Convert.ToHexString(await SHA256.HashDataAsync(input,ct)).ToLowerInvariant();input.Position=0;
        string basePath=$"workday/assignments/{file.AssignmentId:D}/upload-sessions";
        using var declared=await http.PostAsJsonAsync(basePath,new{uploadId=file.UploadId,fileName=file.FileName,sizeBytes=file.SizeBytes,sha256=checksum},ct);declared.EnsureSuccessStatusCode();
        using var document=JsonDocument.Parse(await declared.Content.ReadAsStringAsync(ct));var session=document.RootElement;
        string state=session.GetProperty("state").GetString()!;
        if(state=="Cancelled")throw new InvalidOperationException("The upload was explicitly cancelled.");
        if(state is "Acknowledged" or "Purged")return checksum;
        int chunkBytes=session.GetProperty("chunkBytes").GetInt32();if(chunkBytes<1||chunkBytes>4*1024*1024)throw new InvalidOperationException("Invalid server chunk limit.");
        var received=session.GetProperty("receivedChunks").EnumerateArray().ToDictionary(x=>x.GetProperty("chunkIndex").GetInt32(),x=>x.GetProperty("sha256").GetString());
        byte[] buffer=new byte[chunkBytes];int index=0;
        if(state=="Receiving")while(input.Position<input.Length)
        {
            ct.ThrowIfCancellationRequested();long offset=input.Position;int count=await input.ReadAtLeastAsync(buffer,(int)Math.Min(chunkBytes,input.Length-offset),true,ct);
            string hash=Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0,count))).ToLowerInvariant();
            if(received.TryGetValue(index,out var stored))
            {if(stored!=hash)throw new InvalidOperationException("A stored upload chunk belongs to different bytes.");}
            else
            {
                using var content=new ByteArrayContent(buffer,0,count);content.Headers.ContentType=new("application/octet-stream");
                using var request=new HttpRequestMessage(HttpMethod.Put,$"{basePath}/{file.UploadId:D}/chunks/{index}"){Content=content};request.Headers.Add("X-Chunk-SHA256",hash);
                using var response=await http.SendAsync(request,ct);response.EnsureSuccessStatusCode();
            }
            progress?.Report(new(offset+count,file.SizeBytes,"Uploading"));index++;
        }
        using var completed=await http.PostAsJsonAsync($"{basePath}/{file.UploadId:D}/complete",new{},ct);completed.EnsureSuccessStatusCode();
        using var receipt=JsonDocument.Parse(await completed.Content.ReadAsStringAsync(ct));
        if(receipt.RootElement.GetProperty("sha256").GetString()!=checksum)throw new InvalidOperationException("The API checksum receipt differs from the local original.");
        progress?.Report(new(file.SizeBytes,file.SizeBytes,"Waiting for Studio"));return checksum;
    }
}
