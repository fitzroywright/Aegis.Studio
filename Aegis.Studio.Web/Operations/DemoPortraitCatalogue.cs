using System.Text.Json;
namespace Aegis.Studio.Web.Operations;
// Commons metadata is checked at catalogue creation; no procedural or generated photos.
public static class DemoPortraitCatalogue
{
    private static readonly Lazy<Photo[]> photos=new(()=>
    {
        using var input=typeof(DemoPortraitCatalogue).Assembly.GetManifestResourceStream("Aegis.Studio.Web.Demo.portrait-catalogue.json")??throw new InvalidOperationException("Demo portrait catalogue is missing.");
        return JsonSerializer.Deserialize<Photo[]>(input,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})!;
    });
    public static IReadOnlyList<Photo> Photos=>photos.Value;
    public sealed record Photo(string Title,string Url,string Page,string License,string SourceSha1);
    public static string Url(int index)
    {
        if(index<0||index>=Photos.Count)throw new ArgumentOutOfRangeException(nameof(index),"The licensed source catalogue is exhausted. Add genuine photographs rather than repeating them.");
        return Photos[index].Url;
    }
}
