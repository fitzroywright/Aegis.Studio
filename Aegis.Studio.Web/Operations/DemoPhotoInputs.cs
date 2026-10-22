using Aegis.Studio.Web.Media;
using Npgsql;
using System.Collections.Concurrent;
using Common.Storage;

namespace Aegis.Studio.Web.Operations;

internal static partial class DemoDataExpansion
{
    private sealed record PhotoInput(string Url, string Source, string License, string Page);
    private static string aiPhotoRoot => Environment.GetEnvironmentVariable("AEGIS_STUDIO_DEMO_PHOTOS")
        ?? "/var/lib/aegis-studio/demo-photos";
    private static readonly ConcurrentDictionary<string,string> demoPhotoSourceCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string,string> demoPhotoSourceNameCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string,string> demoPhotoLicenseCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string,string> demoPhotoPageCache = new(StringComparer.OrdinalIgnoreCase);

    private static string PhotoCategory(string? type)
    {
        type ??= "";
        if (type.Contains("Wedding",StringComparison.OrdinalIgnoreCase)||type.Contains("Engagement",StringComparison.OrdinalIgnoreCase)) return "wedding";
        if (type.Contains("Graduation",StringComparison.OrdinalIgnoreCase)||type.Contains("School",StringComparison.OrdinalIgnoreCase)) return "graduation";
        if (type.Contains("Newborn",StringComparison.OrdinalIgnoreCase)) return "family";
        if (type.Contains("Family",StringComparison.OrdinalIgnoreCase)) return "family";
        if (type.Contains("Sports",StringComparison.OrdinalIgnoreCase)||type.Contains("Athlete",StringComparison.OrdinalIgnoreCase)||type.Contains("Team",StringComparison.OrdinalIgnoreCase)) return "sports";
        if (type.Contains("Product",StringComparison.OrdinalIgnoreCase)||type.Contains("Commercial",StringComparison.OrdinalIgnoreCase)||type.Contains("Campaign",StringComparison.OrdinalIgnoreCase)) return "product";
        return "people";
    }

    private static string[] LocalPhotos(string category)
    {
        string directory=Path.Combine(aiPhotoRoot,category);
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory)
            .Where(file=>new[]{".jpg",".jpeg",".png",".webp"}.Contains(Path.GetExtension(file),StringComparer.OrdinalIgnoreCase))
            .OrderBy(file=>file,StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool TryAiPhoto(string? type,int variant,out byte[] bytes,out PhotoInput? input)
    {
        bytes=[];
        input=null;
        string category=PhotoCategory(type);
        string[] files=LocalPhotos(category);
        if(files.Length==0) return false;
        string file=files[Math.Abs(variant % files.Length)];
        try
        {
            using var decoded=StudioImageProcessor.DecodeOriented(File.OpenRead(file));
            using var resized=StudioImageProcessor.ResizeMax(decoded,4096);
            bytes=StudioImageProcessor.EncodeJpeg(resized,95);
            string relative=Path.GetRelativePath(aiPhotoRoot,file).Replace(Path.DirectorySeparatorChar,'/');
            bool bryansPhoto=Path.GetFileName(file).StartsWith("BryansStudio-",StringComparison.OrdinalIgnoreCase);
            input=new PhotoInput(
                "aegis-demo-resource://"+relative,
                bryansPhoto ? "Bryan's Photo Studios presentation bundle" : "Kratos demo photo library",
                bryansPhoto ? "Approved for Studio demo use" : "Approved local demo library photo",
                "aegis-demo-resource://"+relative);
            return true;
        }
        catch(Exception error) when(error is IOException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    internal static void PreparePhotos(Action<DemoSeedProgress>? report)
    {
        ValidateLocalDemoPhotos(report);
        string[] required=["people","wedding","graduation","family","sports","product"];
        foreach(string category in required)
        {
            string[] files=LocalPhotos(category);
            if(files.Length==0)
                throw new InvalidOperationException($"The local demo photo library has no usable photos for category '{category}' at '{Path.Combine(aiPhotoRoot,category)}'. Add at least one image to that Kratos library folder and retry. Demo generation does not download images.");
        }
    }

    private static void ValidateLocalDemoPhotos(Action<DemoSeedProgress>? report)
    {
        if (!Directory.Exists(aiPhotoRoot))
            throw new InvalidOperationException(
                $"Studio cannot find the demo photo library at '{aiPhotoRoot}'. Install the local demo photo library on Kratos before recreating demo data. Demo generation does not download images.");

        string[] files = Directory.EnumerateFiles(aiPhotoRoot, "*", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(aiPhotoRoot, file)
                .StartsWith("source-cache" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(file => new[] { ".jpg", ".jpeg", ".png", ".webp" }
                .Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if(files.Length==0)
            throw new InvalidOperationException($"The local demo photo library at '{aiPhotoRoot}' contains no supported images.");

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

    private static byte[] PhotoBytes(string url, int variant, string? searchTerm, bool allowAi = true)
    {
        if(TryAiPhoto(searchTerm,variant,out var bytes,out var input))
        {
            demoPhotoSourceCache[url]=input!.Url;
            demoPhotoSourceNameCache[url]=input.Source;
            demoPhotoLicenseCache[url]=input.License;
            demoPhotoPageCache[url]=input.Page;
            return bytes;
        }

        string category=PhotoCategory(searchTerm);
        throw new InvalidOperationException(
            $"No local demo photo is available for category '{category}' in '{Path.Combine(aiPhotoRoot,category)}'. Demo generation uses only the Kratos photo library and does not download images.");
    }
}
