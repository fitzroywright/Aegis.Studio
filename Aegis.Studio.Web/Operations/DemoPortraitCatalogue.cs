using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aegis.Studio.Web.Operations;

// Reviewed people photographs grouped by Studio job type. No keyword scraping at runtime.
public static class DemoPortraitCatalogue
{
    private static readonly Lazy<Photo[]> photos=new(()=>
    {
        using var input=typeof(DemoPortraitCatalogue).Assembly.GetManifestResourceStream("Aegis.Studio.Web.Demo.portrait-catalogue.json")??throw new InvalidOperationException("Demo photo catalogue is missing.");
        return JsonSerializer.Deserialize<Photo[]>(input,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})!;
    });
    public static IReadOnlyList<Photo> Photos=>photos.Value;
    public sealed record Photo(string Title,string Url,string Page,string License,string SourceSha1,string Category="Portrait",string Source="Pexels",int Width=0,int Height=0);

    public static string Category(string? jobType)
    {
        string value=(jobType??"").ToLowerInvariant();
        if(value.Contains("wedding")||value.Contains("engagement"))return "Wedding";
        if(value.Contains("graduat"))return "Graduation";
        if(value.Contains("family")||value.Contains("newborn")||value.Contains("maternity"))return "Family";
        if(value.Contains("corporate")||value.Contains("executive")||value.Contains("employee")||value.Contains("headshot"))return "Corporate";
        return "Portrait";
    }
    public static IReadOnlyList<Photo> ForJob(string? jobType,Guid job)
    {
        string category=Category(jobType);
        var pool=Photos.Where(p=>p.Category==category).DistinctBy(p=>p.Url).ToArray();
        if(pool.Length<40)throw new InvalidOperationException($"The {category} demo photo pool requires at least 40 distinct reviewed photographs.");
        // Stable per-job shuffle: retries reuse the same source; different jobs have different ordering.
        return pool.OrderBy(p=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(job.ToString("N")+p.Url))),StringComparer.Ordinal).ToArray();
    }
    public static string Url(int index)
    {
        if(index<0||index>=Photos.Count)throw new ArgumentOutOfRangeException(nameof(index),"The reviewed photo catalogue is exhausted.");
        return Photos[index].Url;
    }
}
