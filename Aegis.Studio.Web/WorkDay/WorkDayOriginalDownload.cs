using System.Security.Cryptography;
namespace Aegis.Studio.Web.WorkDay;

/// <summary>Studio-only pull; never trust a public payload to supply a host or storage path.</summary>
public static class WorkDayOriginalDownload
{
    public static async Task<Stream> FetchAsync(HttpClient http,Guid upload,long size,string sha256,CancellationToken ct)
    {
        if(size<1||size>536_870_912||sha256.Length!=64||!sha256.All(Uri.IsHexDigit))throw new ArgumentException("Invalid private original descriptor.");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromMinutes(30));ct=deadline.Token;
        using var request=new HttpRequestMessage(HttpMethod.Get,$"admin/workday/uploads/{upload:D}/original");
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);response.EnsureSuccessStatusCode();
        if(response.Content.Headers.ContentLength!=size||!response.Headers.TryGetValues("X-Content-SHA256",out var values)||!values.Single().Equals(sha256,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Private original headers do not match the declared content.");
        var options=new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.ReadWrite,Share=FileShare.Read,BufferSize=65536,Options=FileOptions.Asynchronous|FileOptions.DeleteOnClose};
        if(!OperatingSystem.IsWindows())options.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;
        var output=new FileStream(Path.Combine(Path.GetTempPath(),"workday-pull-"+Guid.NewGuid().ToString("N")),options);
        try
        {
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);long received=0;byte[] buffer=new byte[65536];
            await using var input=await response.Content.ReadAsStreamAsync(ct);
            int count;
            while((count=await input.ReadAsync(buffer,ct))>0)
            {
                received+=count;if(received>size)throw new InvalidDataException("Private original exceeds its declared size.");
                hash.AppendData(buffer,0,count);await output.WriteAsync(buffer.AsMemory(0,count),ct);
            }
            if(received!=size||!Convert.ToHexString(hash.GetHashAndReset()).Equals(sha256,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Private original checksum verification failed; acknowledgment withheld.");
            await output.FlushAsync(ct);output.Position=0;return output;
        }
        catch{await output.DisposeAsync();throw;}
    }
}
