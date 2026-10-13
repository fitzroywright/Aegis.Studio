using Aegis.Studio.Web.Operations;
using Common.Storage;
using Npgsql;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class DemoMediaRecoveryTests
{
    [StudioPostgresFact]
    public async Task BrokenDemoMediaIsRemovedByClearRatherThanRepairedInPlace()
    {
        using var context = new IntegratedWorkflowTests.WorkflowContext();
        new GlobalOperationalRoleStore(context.Database).EnsureSchema();
        new StudioEmployeeDirectoryStore(context.Database).EnsureSchema();
        Aegis.Studio.Web.Workflow.WorkflowSchema.Ensure(context.Database);
        var storage = new LocalFileStorage(context.Root);
        using var bytes = new MemoryStream(new byte[] {1,2,3,4});
        var version = context.Versions.Add(context.Job, "demo-broken", "broken.jpg", "image/jpeg", bytes, "demo");
        File.Delete(Path.Combine(context.Root, "VersionStore", version.StoredPath));
        Assert.Throws<StorageException>(() => context.Versions.Open(context.Job, version.Id, out _));
        await DemoIsolationTests.Clear(context.Database, storage, context.Root);
        using var command = new NpgsqlCommand("select count(*) from studio_asset_versions", context.Connection);
        Assert.Equal(0L, command.ExecuteScalar());
        Assert.Empty(await ((IStorageQuery)storage).ListAsync(new StorageListRequest()));
    }
}
