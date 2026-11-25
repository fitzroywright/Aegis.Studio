using Aegis.Studio.Web.Messaging;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class JobPhotoDeliveryTests
{
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
