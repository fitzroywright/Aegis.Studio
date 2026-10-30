using Aegis.Studio.Web.FrontDesk;
using Aegis.Studio.Web.Media;
using Xunit;
namespace Aegis.Studio.Web.Tests;
public sealed class UploadAndDepositDeclarationTests
{
    [Theory]
    [InlineData("+1 (876) 555-0123", "18765550123")]
    [InlineData("+44 20 7946 0958", "442079460958")]
    public void WhatsAppRequiresAnExplicitInternationalDestination(string input,string expected)
        =>Assert.Equal(expected,DepositDeliveryRules.Mobile(input));
    [Theory]
    [InlineData("8765550123")]
    [InlineData("+0123456789")]
    [InlineData("person@example.com")]
    public void AmbiguousDestinationsAreNotGuessed(string input)
        =>Assert.Throws<ArgumentException>(()=>DepositDeliveryRules.Mobile(input));
    [Fact]
    public void ManifestRejectsDuplicateIdsAndUnknownFileSizes()
    {
        var file=new UploadFileDeclaration(Guid.NewGuid(),"photo.jpg",1024);
        var manifest=new UploadBatchDeclaration(Guid.NewGuid(),Guid.NewGuid(),[file]);
        MediaUploadReceipts.Validate(manifest);
        MediaUploadReceipts.Validate(manifest with{Files=[file with{SizeBytes=536_870_912}]});
        Assert.Throws<ArgumentException>(()=>MediaUploadReceipts.Validate(manifest with{Files=[file,file]}));
        Assert.Throws<ArgumentException>(()=>MediaUploadReceipts.Validate(manifest with{Files=[file with{SizeBytes=0}]}));
        Assert.Throws<ArgumentException>(()=>MediaUploadReceipts.Validate(manifest with{Files=[file with{SizeBytes=536_870_913}]}));
        Assert.Throws<ArgumentException>(()=>MediaUploadReceipts.Validate(manifest with{BatchId=Guid.Empty}));
    }
}
