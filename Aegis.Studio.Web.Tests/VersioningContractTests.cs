using System.Reflection;
using Aegis.Studio.Web.Versioning;

namespace Aegis.Studio.Web.Tests;

public sealed class VersioningContractTests
{
    [Fact]
    public void Version_record_tracks_restore_source_without_replacing_history()
    {
        Guid original = Guid.NewGuid();
        StudioAssetVersion restored = new(Guid.NewGuid(), Guid.NewGuid(), "design/poster.psd", 4, "poster.psd", "application/octet-stream", 42, "ABC", "stored", "designer", DateTimeOffset.UtcNow, original, "Restored from version 2");
        Assert.Equal(4, restored.VersionNumber);
        Assert.Equal(original, restored.SourceVersionId);
        Assert.NotEqual(original, restored.Id);
    }

    [Fact]
    public void Store_exposes_add_restore_history_and_open_operations()
    {
        string[] methods = typeof(StudioVersionStore).GetMethods(BindingFlags.Instance | BindingFlags.Public).Select(x => x.Name).ToArray();
        Assert.Contains("Add", methods);
        Assert.Contains("Restore", methods);
        Assert.Contains("GetHistory", methods);
        Assert.Contains("Open", methods);
    }
}
