using System.Reflection;
using Aegis.Studio.Web.Operations;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class DemoImageTests
{
    [Fact]
    public void EveryDemoGalleryFitsTheSmallerFlagFreeSourceCatalogue()
    {
        Type seed=typeof(DemoDataEndpoints).Assembly.GetType("Aegis.Studio.Web.Operations.DemoFullStudioData")!;
        var jobs=(System.Collections.IEnumerable)seed.GetField("Jobs",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
        var count=seed.GetMethod("MediaCount",BindingFlags.NonPublic|BindingFlags.Static)!;
        var source=seed.GetMethod("PortraitSource",BindingFlags.NonPublic|BindingFlags.Static)!;
        foreach(var job in jobs)
        {
            int assets=(int)count.Invoke(null,new[]{job})!;
            Assert.True(assets>=40);
            for(int i=0;i<assets;i++)
            {
                string? url=(string?)source.Invoke(null,new object[]{job,i});
                if(url is not null)Assert.Contains(DemoPortraitCatalogue.Photos,p=>p.Url==url);
            }
        }
    }

    [Fact]
    public void DemoPhotosUseFreeSyntheticPortraitsAndContextualWikimediaSources()
    {
        Type seed=typeof(DemoDataEndpoints).Assembly.GetType("Aegis.Studio.Web.Operations.DemoDataExpansion")!;
        FieldInfo catalogue=seed.GetField("realPhotoUrls",BindingFlags.NonPublic|BindingFlags.Static)!;
        string[] urls=(string[])catalogue.GetValue(null)!;

        Assert.True(urls.Length>=10);
        Assert.All(urls,url=>{
            Assert.StartsWith("https://commons.wikimedia.org/wiki/Special:Redirect/file/",url,StringComparison.Ordinal);
            Assert.True(url.EndsWith(".jpg",StringComparison.OrdinalIgnoreCase)||url.EndsWith(".png",StringComparison.OrdinalIgnoreCase));
        });
        MethodInfo category=seed.GetMethod("PhotoCategory",BindingFlags.NonPublic|BindingFlags.Static)!;
        Assert.Equal("people",category.Invoke(null,new object?[]{"Corporate Headshots"}));
        Assert.Equal("people",category.Invoke(null,new object?[]{"Passport / Visa"}));
        Assert.Equal("people",category.Invoke(null,new object?[]{"Portrait"}));
        Assert.Equal("wedding",category.Invoke(null,new object?[]{"Wedding"}));
        Assert.Equal("graduation",category.Invoke(null,new object?[]{"Graduation"}));
        string assetRoot=(string)seed.GetField("OpenPeopleAssetUrl",BindingFlags.NonPublic|BindingFlags.Static)!.GetRawConstantValue()!;
        Assert.StartsWith("https://raw.githubusercontent.com/Prompt-Haus/OpenPeople/",assetRoot,StringComparison.Ordinal);
    }
    [Fact]
    public void LargeWeddingAndGraduationMediaCannotOverwriteAdjacentJobs()
    {
        Type seed=typeof(DemoDataEndpoints).Assembly.GetType("Aegis.Studio.Web.Operations.DemoFullStudioData")!;
        var allocate=seed.GetMethod("MediaId",BindingFlags.NonPublic|BindingFlags.Static)!;
        var ids=new HashSet<Guid>();
        // The old 100-record stride collided as soon as a wedding exceeded 100 assets.
        foreach(int group in new[]{59,64,65,66})
        foreach(int job in new[]{201,202,203,301,302,303,401,402,403})
        foreach(int asset in Enumerable.Range(0,500))
            Assert.True(ids.Add((Guid)allocate.Invoke(null,new object[]{group,job,asset})!));
    }
}


