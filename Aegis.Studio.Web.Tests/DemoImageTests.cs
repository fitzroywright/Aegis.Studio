using System.Reflection;
using System.Security.Cryptography;
using Aegis.Studio.Web.Operations;
using SixLabors.ImageSharp;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class DemoImageTests
{
    [Fact]
    public void DemoPreviewsAreDistinctDecodablePngImages()
    {
        Type seed = typeof(DemoDataEndpoints).Assembly.GetType("Aegis.Studio.Web.Operations.DemoDataExpansion")!;
        MethodInfo create = seed.GetMethod("CreateIllustration", BindingFlags.NonPublic | BindingFlags.Static)!;
        HashSet<string> hashes = [];
        for (int variant = 0; variant < 24; variant++)
        {
            using Image image = (Image)create.Invoke(null, [variant])!;
            using MemoryStream stream = new();
            image.SaveAsPng(stream);
            stream.Position = 0;
            using Image decoded = Image.Load(stream);
            Assert.Equal(640, decoded.Width);
            Assert.Equal(420, decoded.Height);
            hashes.Add(Convert.ToHexString(SHA256.HashData(stream.ToArray())));
        }
        Assert.Equal(24, hashes.Count);
    }
}
