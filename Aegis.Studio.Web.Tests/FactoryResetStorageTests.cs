using System.Reflection;
using Aegis.Studio.Web.Workflow;
using Common.Storage;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public class FactoryResetStorageTests
{
    private static readonly Type Factory = typeof(WorkflowSchema).Assembly.GetType("Aegis.Studio.Web.Workflow.StudioFactoryReset")!;
    private static void Validate(IFileStorage storage, bool exclusive) =>
        Factory.GetMethod("ValidateStorage", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [storage, new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Storage:FactoryResetExclusiveRoot"] = exclusive.ToString() }).Build()]);
    private static Task<long> Purge(IFileStorage storage, CancellationToken token = default) =>
        (Task<long>)Factory.GetMethod("PurgeStorageAsync", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [storage, token])!;

    [Fact]
    public async Task BucketOnlyProviderIsAcceptedAndPurgedWithoutPerFileLifecycle()
    {
        var storage = new BucketStorage();
        Validate(storage, true);
        using var cancellation = new CancellationTokenSource();
        Assert.Equal(7L, await Purge(storage, cancellation.Token));
        Assert.Equal(1, storage.Calls);
        Assert.Equal(cancellation.Token, storage.Token);
    }

    [Fact]
    public void BucketProviderStillRequiresDedicatedStorage()
    {
        var storage = new BucketStorage();
        var error = Assert.Throws<TargetInvocationException>(() => Validate(storage, false));
        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Equal(0, storage.Calls);
    }

    [Fact]
    public void ProviderWithoutEitherPurgeCapabilityIsRejected()
    {
        var error = Assert.Throws<TargetInvocationException>(() => Validate(new UnsupportedStorage(), true));
        Assert.Contains("No data was deleted", error.InnerException!.Message);
    }

    [Fact]
    public async Task BucketPurgeFailureIsPropagated()
    {
        var storage = new BucketStorage { Failure = new IOException("Bucket purge failed") };
        var error = await Assert.ThrowsAsync<IOException>(() => Purge(storage));
        Assert.Equal("Bucket purge failed", error.Message);
        Assert.Equal(1, storage.Calls);
    }

    private class UnsupportedStorage : IFileStorage
    {
        public Task<StoredFile> StoreAsync(StorageWriteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StoredFile?> GetMetadataAsync(string storageKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<StoredFile>> GetVersionsAsync(string storageKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StorageHealth> CheckHealthAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class BucketStorage : UnsupportedStorage, IStorageBucketReset
    {
        public int Calls { get; private set; }
        public CancellationToken Token { get; private set; }
        public IOException? Failure { get; init; }
        public Task<long> PurgeBucketAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            Token = cancellationToken;
            return Failure is null ? Task.FromResult(7L) : Task.FromException<long>(Failure);
        }
    }
}
