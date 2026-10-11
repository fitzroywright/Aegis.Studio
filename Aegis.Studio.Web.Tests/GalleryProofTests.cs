using Aegis.Studio.Web.Galleries;
using SkiaSharp;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class GalleryProofTests
{
    [Theory]
    [InlineData("original", false)]
    [InlineData("preview", true)]
    [InlineData("THUMBNAIL", true)]
    [InlineData("content", false)]
    public void LegacyPublicGalleryOnlyServesProofVariants(string variant, bool expected)
        => Assert.Equal(expected, GalleryProofs.IsProofVariant(variant));

    [Fact]
    public void PublicProofIsReducedAndVisiblyMarkedWithoutSystemFonts()
    {
        using var image = new SKBitmap(2400, 1600);
        using (var canvas = new SKCanvas(image)) canvas.Clear(SKColors.Red);
        using var encoded = SKImage.FromBitmap(image);
        using var png = encoded.Encode(SKEncodedImageFormat.Png, 100);
        using var source = new MemoryStream(png.ToArray());
        using var proof = SKBitmap.Decode(GalleryProofs.Render(source));
        Assert.Equal(1200, proof.Width);
        Assert.Equal(800, proof.Height);
        var plain = proof.GetPixel(10, 10);
        // White lettering crosses the central quarter of the photograph.
        Assert.Contains(Enumerable.Range(0, proof.Width), x => proof.GetPixel(x, 178).Green > plain.Green + 50);
    }
}
