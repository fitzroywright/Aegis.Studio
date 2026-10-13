using System.Reflection;
using Aegis.Studio.Web.Operations;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class DemoImageTests
{
    [Fact]
    public void DemoPhotoCatalogueUsesRealWikimediaSourcesAndHasNoSyntheticGenerator()
    {
        Type seed=typeof(DemoDataEndpoints).Assembly.GetType("Aegis.Studio.Web.Operations.DemoDataExpansion")!;
        FieldInfo catalogue=seed.GetField("realPhotoUrls",BindingFlags.NonPublic|BindingFlags.Static)!;
        string[] urls=(string[])catalogue.GetValue(null)!;

        Assert.True(urls.Length>=10);
        Assert.All(urls,url=>{
            Assert.StartsWith("https://commons.wikimedia.org/wiki/Special:Redirect/file/",url,StringComparison.Ordinal);
            Assert.True(url.EndsWith(".jpg",StringComparison.OrdinalIgnoreCase)||url.EndsWith(".png",StringComparison.OrdinalIgnoreCase));
        });
        Assert.Null(seed.GetMethod("CreateIllustration",BindingFlags.NonPublic|BindingFlags.Static));
    }

    [Theory]
    [InlineData("Wedding","Portraits and IDs","Wedding")]
    [InlineData("Executive Headshots","Weddings and Events","Portrait")]
    [InlineData("Product Photography","Commercial and Products","Product")]
    [InlineData("Corporate Portrait","Commercial and Products","Portrait")]
    [InlineData("Graduation Retakes","Graduations","Graduation")]
    [InlineData("School Portraits","Schools and Corporate","School")]
    [InlineData("Sports Team","Sports and Groups","Sports")]
    [InlineData("Newborn","Portraits and IDs","Newborn")]
    [InlineData("Family + Pets","Portraits and IDs","Family")]
    [InlineData("Maternity","Portraits and IDs","Maternity")]
    [InlineData("Birthday","Weddings and Events","Birthday")]
    [InlineData("Corporate Event","Schools and Corporate","Event")]
    [InlineData("Unclassified","Weddings and Events","Wedding")]
    [InlineData("","Commercial and Products","Product")]
    public void DemoPhotographsFollowJobTypeBeforeWorkspace(string jobType,string workspace,string expected)
    {
        Type seed=typeof(DemoDataEndpoints).Assembly.GetType("Aegis.Studio.Web.Operations.DemoDataExpansion")!;
        var category=seed.GetMethod("PhotoCategory",BindingFlags.NonPublic|BindingFlags.Static)!;
        Assert.Equal(expected,category.Invoke(null,new object[]{jobType,workspace}));
        var sources=seed.GetMethod("PhotoSources",BindingFlags.NonPublic|BindingFlags.Static)!;
        string[] urls=(string[])sources.Invoke(null,new object[]{jobType,workspace})!;
        Assert.NotEmpty(urls);
        if(expected!="Portrait")
        {
            var portraits=DemoPortraitCatalogue.Photos.Select(photo=>photo.Url).ToHashSet();
            Assert.All(urls,url=>Assert.DoesNotContain(url,portraits));
        }
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

