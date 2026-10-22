using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aegis.Studio.Web.Media;

namespace Aegis.Studio.Web.Operations;

internal static partial class DemoDataExpansion
{
    private sealed record PhotoInput(string Url, string Source, string License, string Page);
    private static readonly string aiPhotoRoot = Environment.GetEnvironmentVariable("AEGIS_STUDIO_DEMO_PHOTOS")
        ?? "/var/lib/aegis-studio/demo-photos";
    private static readonly string inputDirectory = Path.Combine(aiPhotoRoot, "source-cache");
    private const int OpenPeoplePhotoCount = 100;
    private const string OpenPeopleAssetUrl = "https://raw.githubusercontent.com/Prompt-Haus/OpenPeople/main/openpeople/data/curated/P{0:000}/assets/studio_portrait.jpg";
    private const string OpenPeoplePageUrl = "https://github.com/Prompt-Haus/OpenPeople";
    private static readonly HttpClient openPeopleClient = new() { Timeout = TimeSpan.FromSeconds(45) };
    private static string PhotoCategory(string? type)
    {
        type ??= "";
        if (type.Contains("Wedding",StringComparison.OrdinalIgnoreCase)||type.Contains("Engagement",StringComparison.OrdinalIgnoreCase)) return "wedding";
        if (type.Contains("Graduation",StringComparison.OrdinalIgnoreCase)||type.Contains("School",StringComparison.OrdinalIgnoreCase)) return "graduation";
        if (type.Contains("Newborn",StringComparison.OrdinalIgnoreCase)) return "newborn";
        if (type.Contains("Family",StringComparison.OrdinalIgnoreCase)) return "family";
        if (type.Contains("Sports",StringComparison.OrdinalIgnoreCase)||type.Contains("Athlete",StringComparison.OrdinalIgnoreCase)||type.Contains("Team",StringComparison.OrdinalIgnoreCase)) return "sports";
        if (type.Contains("Product",StringComparison.OrdinalIgnoreCase)||type.Contains("Commercial",StringComparison.OrdinalIgnoreCase)) return "product";
        if (type.Contains("Corporate",StringComparison.OrdinalIgnoreCase)||type.Contains("Headshot",StringComparison.OrdinalIgnoreCase)) return "people";
        if (type.Contains("Passport",StringComparison.OrdinalIgnoreCase)||type.Contains("ID",StringComparison.OrdinalIgnoreCase)) return "people";
        return "people";
    }
    private static bool TryAiPhoto(string? type,int variant,out byte[] bytes,out PhotoInput? input)
    {
        bytes=[];
        input=null;
        string directory=Path.Combine(aiPhotoRoot,PhotoCategory(type));
        if (!Directory.Exists(directory)) return false;
        string[] files=Directory.EnumerateFiles(directory)
            .Where(file=>new[]{".jpg",".jpeg",".png",".webp"}.Contains(Path.GetExtension(file),StringComparer.OrdinalIgnoreCase))
            .OrderBy(file=>file,StringComparer.OrdinalIgnoreCase).ToArray();
        if (variant < 0 || variant >= files.Length) return false;
        string file=files[variant];
        try
        {
            using var decoded=StudioImageProcessor.DecodeOriented(File.OpenRead(file));
            using var resized=StudioImageProcessor.ResizeMax(decoded,4096);
            bytes=StudioImageProcessor.EncodeJpeg(resized,95);
            string name=Path.GetFileNameWithoutExtension(file);
            bool openPeople=name.StartsWith("OpenPeople-P",StringComparison.OrdinalIgnoreCase);
            bool bryansPhoto=name.StartsWith("BryansStudio-",StringComparison.OrdinalIgnoreCase);
            string? sourceUrl=null;
            if(openPeople && int.TryParse(name.AsSpan("OpenPeople-P".Length),out int person))
                sourceUrl=string.Format(System.Globalization.CultureInfo.InvariantCulture,OpenPeopleAssetUrl,person);
            string relative=Path.GetRelativePath(aiPhotoRoot,file).Replace(Path.DirectorySeparatorChar,'/');
            input=new PhotoInput(sourceUrl ?? new Uri(Path.GetFullPath(file)).AbsoluteUri,
                openPeople ? "Prompt-Haus OpenPeople" : bryansPhoto ? "Bryan's Photo Studios presentation bundle" : "AI generated",
                openPeople ? "CC BY 4.0" : bryansPhoto ? "Approved for Studio demo use" : "AI-generated demo image",
                openPeople ? OpenPeoplePageUrl : bryansPhoto ? "aegis-demo-resource://" + relative : file);
            return true;
        }
        catch(Exception error) when(error is IOException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    internal static void PreparePhotos(Action<DemoSeedProgress>? report)
    {
        // Prepare and validate all synthetic portraits and contextual source photos before any demo records are replaced.
        EnsureOpenPeoplePortraits(report);
        ValidateLocalDemoPhotos(report);
        string[] urls = realPhotoUrls.Concat(weddingPhotoUrls).Distinct().ToArray();
        for (int i = 0; i < urls.Length; i++)
        {
            report?.Invoke(new(1, "Preparing photographs", i, urls.Length, $"Verifying contextual photograph source {i+1}/{urls.Length} before recreating data."));
            _ = PhotoBytes(urls[i], i, null, allowAi: false);
        }
    }

    private static void ValidateLocalDemoPhotos(Action<DemoSeedProgress>? report)
    {
        try
        {
            Directory.CreateDirectory(aiPhotoRoot);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new InvalidOperationException(
                $"Studio cannot access the demo photo library at '{aiPhotoRoot}'. Run the Kratos photo-library setup script and grant write access to the aegis-studio service account before recreating demo data.",
                error);
        }

        string[] files = Directory.EnumerateFiles(aiPhotoRoot, "*", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(aiPhotoRoot, file)
                .StartsWith("source-cache" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(file => new[] { ".jpg", ".jpeg", ".png", ".webp" }
                .Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            .ToArray();
        for (int index = 0; index < files.Length; index++)
        {
            report?.Invoke(new(1, "Preparing photographs", index, files.Length,
                $"Validating local demo photo {index + 1}/{files.Length}."));
            try
            {
                using var image = StudioImageProcessor.DecodeOriented(File.OpenRead(files[index]));
                if (image.Width <= 0 || image.Height <= 0)
                    throw new InvalidDataException("Image dimensions are invalid.");
            }
            catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException)
            {
                throw new InvalidDataException($"Demo photo failed validation: '{files[index]}'.", error);
            }
        }
    }

    private static void EnsureOpenPeoplePortraits(Action<DemoSeedProgress>? report)
    {
        string directory=Path.Combine(aiPhotoRoot,"people");
        Directory.CreateDirectory(directory);
        bool IsValidPortrait(string file)
        {
            if(!File.Exists(file) || new FileInfo(file).Length<=10_000)return false;
            try
            {
                using var image=StudioImageProcessor.DecodeOriented(File.OpenRead(file));
                return image.Width>0 && image.Height>0;
            }
            catch(Exception error) when(error is IOException or ArgumentException or InvalidOperationException)
            { return false; }
        }
        int available=Enumerable.Range(1,OpenPeoplePhotoCount).Count(person =>
            IsValidPortrait(Path.Combine(directory,$"OpenPeople-P{person:000}.jpg")));
        if(available==OpenPeoplePhotoCount)return;

        for(int person=1;person<=OpenPeoplePhotoCount;person++)
        {
            string file=Path.Combine(directory,$"OpenPeople-P{person:000}.jpg");
            if(IsValidPortrait(file))continue;
            report?.Invoke(new(1,"Preparing photographs",person-1,OpenPeoplePhotoCount,$"Downloading free synthetic portrait {person}/{OpenPeoplePhotoCount}."));
            string url=string.Format(System.Globalization.CultureInfo.InvariantCulture,OpenPeopleAssetUrl,person);
            byte[] bytes=openPeopleClient.GetByteArrayAsync(url).GetAwaiter().GetResult();
            if(bytes.Length<10_000)throw new InvalidDataException($"OpenPeople portrait {person} was unexpectedly small.");
            using var stream=new MemoryStream(bytes,false);
            using var decoded=StudioImageProcessor.DecodeOriented(stream);
            using var resized=StudioImageProcessor.ResizeMax(decoded,4096);
            byte[] validated=StudioImageProcessor.EncodeJpeg(resized,95);
            File.WriteAllBytes(file,validated);
        }
    }

    private static byte[] PhotoBytes(string url, int variant, string? searchTerm, bool allowAi = true)
    {
        if (allowAi && TryAiPhoto(searchTerm,variant,out var aiBytes,out var aiInput))
        {
            demoPhotoSourceCache[url]=aiInput!.Url;
            demoPhotoSourceNameCache[url]=aiInput.Source;
            demoPhotoLicenseCache[url]=aiInput.License;
            demoPhotoPageCache[url]=aiInput.Page;
            return aiBytes;
        }
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
        // When Commons rate-limits requests, reuse a previously validated portrait
        // for general jobs. The curated category jobs must never cross categories.
        HttpResponseMessage response;
        try { response = DownloadDemoPhoto(url, variant, searchTerm); }
        catch (HttpRequestException error) when (searchTerm is not null &&
            !new[]{"Wedding","Engagement","Graduation","School","Product","Commercial","Family","Newborn","Sports","Athlete","Team"}
                .Any(term => searchTerm.Contains(term, StringComparison.OrdinalIgnoreCase)) &&
            error.ToString().Contains("429", StringComparison.Ordinal))
        {
            foreach (var photo in DemoPortraitCatalogue.Photos.Take(24))
            {
                string cached = Path.Combine(inputDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(photo.Url))) + ".jpg");
                if (!File.Exists(cached) || !File.Exists(cached + ".json")) continue;
                try
                {
                    bytes = File.ReadAllBytes(cached);
                    using var decoded = StudioImageProcessor.DecodeOriented(new MemoryStream(bytes, false));
                    input = JsonSerializer.Deserialize<PhotoInput>(File.ReadAllText(cached + ".json"))!;
                    if (input is null) continue;
                    File.WriteAllBytes(path, bytes);
                    File.WriteAllText(path + ".json", JsonSerializer.Serialize(input));
                    Remember(input);
                    return bytes;
                }
                catch (Exception) { /* Try another validated cached photograph. */ }
            }
            throw;
        }
        using (response)
        {
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
        }

        void Remember(PhotoInput info)
        {
            demoPhotoSourceCache[url] = info.Url;
            demoPhotoSourceNameCache[url] = info.Source;
            demoPhotoLicenseCache[url] = info.License;
            demoPhotoPageCache[url] = info.Page;
        }
    }
}
