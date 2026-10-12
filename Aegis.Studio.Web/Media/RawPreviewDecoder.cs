using System.ComponentModel;
using System.Diagnostics;
using SkiaSharp;

namespace Aegis.Studio.Web.Media;

// RAW rendering is deliberately confined to the durable background worker.
internal sealed class RawPreviewDecoder(IConfiguration configuration, IWebHostEnvironment environment)
{
    public static bool IsRaw(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() is
        ".3fr" or ".arw" or ".cr2" or ".cr3" or ".crw" or ".dng" or ".erf" or ".fff" or
        ".iiq" or ".kdc" or ".mos" or ".mrw" or ".nef" or ".nrw" or ".orf" or ".pef" or
        ".raf" or ".raw" or ".rw2" or ".rwl" or ".sr2" or ".srf" or ".srw" or ".x3f";

    public async Task<SKBitmap> DecodeAsync(string sourcePath, CancellationToken token)
    {
        string helper = Path.Combine(environment.ContentRootPath, "Media", "raw_preview.py");
        if (!File.Exists(helper)) throw new InvalidOperationException("RAW preview helper is not installed.");
        string output = Path.Combine(Path.GetDirectoryName(sourcePath)!, "preview.jpg");
        var start = new ProcessStartInfo(configuration["Media:RawPreview:PythonExecutable"] ?? "python3")
        {
            UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(sourcePath)!
        };
        // Never interpolate filenames into a shell command.
        start.ArgumentList.Add(helper);
        start.ArgumentList.Add(sourcePath);
        start.ArgumentList.Add(output);
        start.Environment["OMP_NUM_THREADS"] = "1";
        start.Environment["OPENBLAS_NUM_THREADS"] = "1";
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Win32Exception error) { throw new InvalidOperationException("RAW preview Python runtime is unavailable.", error); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        int seconds = int.TryParse(configuration["Media:RawPreview:TimeoutSeconds"], out var value)
            ? Math.Clamp(value, 5, 300) : 90;
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            token.ThrowIfCancellationRequested();
            throw new TimeoutException("RAW preview generation timed out; the original is retained.");
        }
        if (process.ExitCode == 2) throw new StudioUnsupportedImageException("RAW format is unsupported, damaged, or exceeds the decoder limits.");
        if (process.ExitCode != 0) throw new InvalidOperationException("RAW preview generation failed. Check the Python runtime and RAW preview dependencies.");
        var info = new FileInfo(output);
        if (!info.Exists || info.Length == 0 || info.Length > 20_000_000)
            throw new InvalidOperationException("RAW decoder did not produce a valid bounded preview.");
        using var stream = File.OpenRead(output);
        return StudioImageProcessor.DecodeOriented(stream);
    }
}
