using System.Diagnostics;
using System.Text.Json;
namespace Aegis.Studio.Web.Media;

internal static class PhotoContainerProbe
{
    public static bool RequiresValidation(string name)=>Path.GetExtension(name).ToLowerInvariant() is
        ".cr2" or ".cr3" or ".nef" or ".nrw" or ".arw" or ".raf" or ".orf" or ".rw2" or ".dng" or ".tif" or ".tiff" or ".heic" or ".heif";
    public static async Task<string> InspectAsync(IFormFile file,CancellationToken ct)
    {
        var options=new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.ReadWrite,Share=FileShare.Read,Options=FileOptions.DeleteOnClose|FileOptions.Asynchronous};
        if(!OperatingSystem.IsWindows())options.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;
        string path=Path.Combine(Path.GetTempPath(),"studio-photo-probe-"+Guid.NewGuid().ToString("N"));
        await using var staged=new FileStream(path,options);
        await using(var input=file.OpenReadStream())await input.CopyToAsync(staged,65536,ct);
        await staged.FlushAsync(ct);
        return await InspectPathAsync(path,file.FileName,ct);
    }
    public static async Task<string> InspectPathAsync(string path,string name,CancellationToken ct)
    {
        string helper=Path.Combine(AppContext.BaseDirectory,"Media","photo_probe.py");
        if(!File.Exists(helper))throw new IOException("Photo container validator is not installed; original remains pending.");
        var start=new ProcessStartInfo(Environment.GetEnvironmentVariable("Media__RawPreview__PythonExecutable")??"python3")
            {UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
        start.ArgumentList.Add(helper);start.ArgumentList.Add(path);start.ArgumentList.Add(Path.GetFileName(name));
        PhotoProcessEnvironment.Configure(start);
        Process started;
        try{started=Process.Start(start)??throw new IOException("Photo validation runtime unavailable.");}
        catch(System.ComponentModel.Win32Exception error){throw new IOException("Photo validation runtime unavailable; original remains pending.",error);}
        using var process=started;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            Task<string> output=process.StandardOutput.ReadToEndAsync(deadline.Token);
            Task<string> errors=process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);string text=await output;await errors;
            if(process.ExitCode==2)throw new ArgumentException("Photograph contents do not match the declared camera format.");
            if(process.ExitCode!=0||text.Length>65536)throw new IOException("Photo container validation unavailable; original remains pending.");
            using var result=JsonDocument.Parse(text);return result.RootElement.GetProperty("contentType").GetString()??"application/octet-stream";
        }
        catch(OperationCanceledException)
        {
            if(!process.HasExited)process.Kill(true);await process.WaitForExitAsync(CancellationToken.None);ct.ThrowIfCancellationRequested();
            throw new IOException("Photo validation timeout; original remains pending.");
        }
    }
}
