using System.Text.Json;
namespace Aegis.Studio.Web.Operations;
// Curated civilian photographs without flag backgrounds; retain license and author credit.
public static class DemoPortraitCatalogue
{
    private static readonly Lazy<Photo[]> photos=new(()=>
    {
        using var input=typeof(DemoPortraitCatalogue).Assembly.GetManifestResourceStream("Aegis.Studio.Web.Demo.portrait-catalogue.json")??throw new InvalidOperationException("Demo portrait catalogue is missing.");
        return JsonSerializer.Deserialize<Photo[]>(input,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})!;
    });
    public static IReadOnlyList<Photo> Photos=>photos.Value;
    public sealed record Photo(string Title,string Url,string Page,string License,string Credit);
    public static string Url(int index)
    {
        if(index<0||index>=Photos.Count)throw new ArgumentOutOfRangeException(nameof(index),"The requested photograph is outside the reviewed source catalogue.");
        return Photos[index].Url;
    }
}

