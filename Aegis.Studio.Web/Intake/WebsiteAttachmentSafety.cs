using System.Diagnostics;
namespace Aegis.Studio.Web.Intake;
public static class WebsiteAttachmentSafety
{
    public static bool TypeMatches(byte[] bytes,string type,string name)
    {
        string extension=Path.GetExtension(name).ToLowerInvariant();
        return type switch
        {
            "image/jpeg"=>(extension is ".jpg" or ".jpeg") && bytes.Length>=3&&bytes[0]==255&&bytes[1]==216&&bytes[2]==255,
            "image/png"=>extension==".png"&&bytes.Length>=8&&bytes.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}),
            "application/pdf"=>extension==".pdf"&&bytes.Length>=5&&System.Text.Encoding.ASCII.GetString(bytes,0,5)=="%PDF-",
            _=>false
        };
    }
    public static async Task<bool> Scan(byte[] bytes,string type,string name,IConfiguration configuration,CancellationToken ct)
    {
        if(!TypeMatches(bytes,type,name))return false;
        string? executable=configuration["PublicSite:MalwareScannerPath"];if(string.IsNullOrWhiteSpace(executable))return false;
        string directory=Path.Combine(Path.GetTempPath(),"studio-quarantine-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);if(!OperatingSystem.IsWindows())File.SetUnixFileMode(directory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        try
        {
            string path=Path.Combine(directory,"upload");await File.WriteAllBytesAsync(path,bytes,ct);
            var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};start.ArgumentList.Add("--no-summary");start.ArgumentList.Add(path);
            using var process=Process.Start(start)??throw new InvalidOperationException("Scanner failed to start.");
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(30));
            // Consume output without exposing scanner paths or upload contents in application logs.
            var stdout=process.StandardOutput.ReadToEndAsync(timeout.Token);var stderr=process.StandardError.ReadToEndAsync(timeout.Token);
            try{await process.WaitForExitAsync(timeout.Token);await Task.WhenAll(stdout,stderr);return process.ExitCode==0;}
            catch(OperationCanceledException){try{process.Kill(true);}catch(InvalidOperationException){}if(ct.IsCancellationRequested)throw;return false;}
        }
        finally{Directory.Delete(directory,true);}
    }
}
