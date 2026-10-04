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
}
