using Aegis.Studio.Web.Messaging;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class JobPhotoDeliveryTests
{
    [Theory]
    [InlineData("Full")]
    [InlineData("Compressed")]
    [InlineData("Proof")]
    public void AcceptsSupportedQuality(string quality)=>JobPhotoDelivery.ValidateQuality(quality);

    [Fact]
    public void RejectsUnknownQualityAndUnsafeGalleryOrigins()
    {
        Assert.Throws<ArgumentException>(()=>JobPhotoDelivery.ValidateQuality("OriginalRaw"));
        Assert.Throws<ArgumentException>(()=>JobPhotoDelivery.GalleryUrl("http://example.test"));
        Assert.Throws<ArgumentException>(()=>JobPhotoDelivery.GalleryUrl("https://example.test/private"));
        Assert.Equal("https://example.test/#galleries",JobPhotoDelivery.GalleryUrl("https://example.test/"));
    }

    [Fact]
    public void AcceptsOneOrTenDistinctVersions()
    {
        JobPhotoDelivery.ValidateSelection([Guid.NewGuid()]);
        JobPhotoDelivery.ValidateSelection(Enumerable.Range(0,10).Select(_=>Guid.NewGuid()).ToArray());
    }

    [Fact]
    public void RejectsMissingEmptyDuplicateOrOversizedSelection()
    {
        Guid id=Guid.NewGuid();
        Assert.Throws<ArgumentException>(()=>JobPhotoDelivery.ValidateSelection(null));
        Assert.Throws<ArgumentException>(()=>JobPhotoDelivery.ValidateSelection([]));
        Assert.Throws<ArgumentException>(()=>JobPhotoDelivery.ValidateSelection([Guid.Empty]));
        Assert.Throws<ArgumentException>(()=>JobPhotoDelivery.ValidateSelection([id,id]));
        Assert.Throws<ArgumentException>(()=>JobPhotoDelivery.ValidateSelection(Enumerable.Range(0,11).Select(_=>Guid.NewGuid()).ToArray()));
    }
}
