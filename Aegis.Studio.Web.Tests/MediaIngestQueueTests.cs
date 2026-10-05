using Aegis.Studio.Web.Media;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class MediaIngestQueueTests
{
    [Fact]
    public async Task SaturationRejectsExcessRequestsAndReleaseAdmitsTheQueuedUpload()
    {
        var queue = new MediaIngestQueue(1, 1);
        using var active = await queue.AcquireAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.NotNull(active);
        var waiting = queue.AcquireAsync(Timeout.InfiniteTimeSpan, CancellationToken.None);
        Assert.False(waiting.IsCompleted);
        Assert.Null(await queue.AcquireAsync(TimeSpan.Zero, CancellationToken.None));
        active.Dispose();
        using var next = await waiting;
        Assert.NotNull(next);
        Assert.Null(await queue.AcquireAsync(TimeSpan.Zero, CancellationToken.None));
        next.Dispose();
        using var afterRelease = await queue.AcquireAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.NotNull(afterRelease);
    }

    [Fact]
    public async Task CancellationAndTimeoutReturnAdmissionCapacity()
    {
        var queue = new MediaIngestQueue(1, 1);
        using var active = await queue.AcquireAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.NotNull(active);
        using var cancellation = new CancellationTokenSource();
        var waiting = queue.AcquireAsync(Timeout.InfiniteTimeSpan, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);
        Assert.Null(await queue.AcquireAsync(TimeSpan.Zero, CancellationToken.None));
        var replacement = queue.AcquireAsync(Timeout.InfiniteTimeSpan, CancellationToken.None);
        Assert.False(replacement.IsCompleted);
        active.Dispose();
        using var next = await replacement;
        Assert.NotNull(next);
    }
}
