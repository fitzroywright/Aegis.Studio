using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aegis.Studio.Web.Media;

namespace Aegis.Studio.Web.Operations;

internal static partial class DemoDataExpansion
{
    private sealed record PhotoInput(string Url, string Source, string License, string Page);
    private static readonly string inputDirectory = Path.Combine(Path.GetTempPath(), "aegis-studio-photo-inputs");

    internal static void PreparePhotos(Action<DemoSeedProgress>? report)
    {
        // Downloaded source inputs are independent of application/demo records.
        // Resolve and decode them before opening scenario transactions or deleting the old dataset.
        string[] urls = realPhotoUrls.Concat(weddingPhotoUrls).Concat(DemoPortraitCatalogue.Photos.Select(p => p.Url)).Distinct().ToArray();
        for (int i = 0; i < urls.Length; i++)
        {
            report?.Invoke(new(1, "Preparing photographs", i, urls.Length, $"Verifying photograph source {i+1}/{urls.Length} before recreating data."));
            _ = PhotoBytes(urls[i], i, null);
        }
    }

    private static byte[] PhotoBytes(string url, int variant, string? searchTerm)
    {
        if (weddingPhotoCache.TryGetValue(url, out var bytes)) return bytes;
        Directory.CreateDirectory(inputDirectory);
        string path = Path.Combine(inputDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + ".jpg");
        PhotoInput input;
        if (File.Exists(path) && File.Exists(path + ".json"))
        {
            try
            {
                bytes = File.ReadAllBytes(path);
                using var stream = new MemoryStream(bytes, false);
                using var decoded = StudioImageProcessor.DecodeOriented(stream);
                input = JsonSerializer.Deserialize<PhotoInput>(File.ReadAllText(path + ".json"))!;
                if (input is not null) { Remember(input); return bytes; }
            }
            catch (Exception error) when (error is IOException or JsonException or ArgumentException or InvalidOperationException)
            { File.Delete(path); File.Delete(path + ".json"); }
        }
        using var response = DownloadDemoPhoto(url, variant, searchTerm);
        byte[] source = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        using var sourceStream = new MemoryStream(source, false);
        using var photograph = StudioImageProcessor.DecodeOriented(sourceStream);
        using var resized = StudioImageProcessor.ResizeMax(photograph, 4096);
        bytes = StudioImageProcessor.EncodeJpeg(resized, 95);
        input = new(response.RequestMessage?.RequestUri?.ToString() ?? url,
            Header(response, "X-Aegis-Demo-Source", "Catalogue"), Header(response, "X-Aegis-Demo-License", "Catalogue licence"), Header(response, "X-Aegis-Demo-Page", url));
        File.WriteAllBytes(path, bytes);
        File.WriteAllText(path + ".json", JsonSerializer.Serialize(input));
        Remember(input);
        if (input.Url != url)
        {
            string alias = Path.Combine(inputDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input.Url))) + ".jpg");
            File.WriteAllBytes(alias, bytes);
            File.WriteAllText(alias + ".json", JsonSerializer.Serialize(input));
        }
        return bytes;

        void Remember(PhotoInput info)
        {
            demoPhotoSourceCache[url] = info.Url;
            demoPhotoSourceNameCache[url] = info.Source;
            demoPhotoLicenseCache[url] = info.License;
            demoPhotoPageCache[url] = info.Page;
        }
    }
}
