using Aegis.Studio.Web.Media;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class MediaProductionTests
{
    [Fact]
    public void RenamedJpegRetainsIdentityAndReexportUpdatesIdentityWithoutChangingPixels()
    {
        byte[] original=[0xff,0xd8,0xff,0xda,0,2,14,25,36,0xff,0xd9];
        Guid first=Guid.NewGuid(),second=Guid.NewGuid();
        byte[] exported=MediaProductionRules.StampJpeg(original,first);
        Assert.Equal(first,MediaProductionRules.ReadExportToken(exported));
        byte[] reexported=MediaProductionRules.StampJpeg(exported,second);
        Assert.Equal(second,MediaProductionRules.ReadExportToken(reexported));
        Assert.Equal(original[2..],reexported[^9..]);
        Assert.Equal(reexported,MediaProductionRules.StampJpeg(reexported,second));
    }
    [Fact]
    public void UnknownFormatsAndStrippedIdentityAreNeverMatchedByFilename()
    {
        byte[] png=[137,80,78,71,13,10,26,10];
        Assert.Equal(png,MediaProductionRules.StampJpeg(png,Guid.NewGuid()));
        Assert.Null(MediaProductionRules.ReadExportToken(png));
        Assert.Null(MediaProductionRules.ReadExportToken([0xff,0xd8,0xff,0xd9]));
    }
    [Fact]
    public void FiftyPersonRosterSupportsQuotedNamesAndCustomFields()
    {
        string csv="Subject ID,Name,Department,Email\r\n"+string.Join("\r\n",Enumerable.Range(1,50).Select(i=>$"{i},\"Employee, {i}\",\"HR\nTeam\",person{i}@example.invalid"));
        var rows=MediaProductionRules.ReadRoster(csv);
        Assert.Equal(51,rows.Count);Assert.Equal("Employee, 50",rows[50][1]);Assert.Equal("HR\nTeam",rows[1][2]);
        Assert.Throws<ArgumentException>(()=>MediaProductionRules.ReadRoster("ID,Name\n1,\"Broken"));
        Assert.Throws<ArgumentException>(()=>MediaProductionRules.ValidateState("PretendDone"));
    }
}
