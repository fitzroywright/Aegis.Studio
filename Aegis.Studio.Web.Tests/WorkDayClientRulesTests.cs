using Aegis.Studio.WorkDay.Core;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class WorkDayClientRulesTests
{
    [Fact]
    public void Offline_with_healthy_local_store_is_not_failed()
        => Assert.Equal("Offline", WorkDayClientRules.ConnectivityState(false, true));

    [Fact]
    public void No_server_with_pending_work_is_waiting_not_healthy()
        => Assert.Equal("Waiting", WorkDayClientRules.SynchronizationState(false, 4, 0));

    [Theory]
    [InlineData(LocalSyncState.Pending, true)]
    [InlineData(LocalSyncState.Failed, true)]
    [InlineData(LocalSyncState.Acknowledged, false)]
    [InlineData(LocalSyncState.Conflict, false)]
    public void Pending_semantics_are_explicit(LocalSyncState state, bool expected)
        => Assert.Equal(expected, WorkDayClientRules.IsPending(state));

    [Fact]
    public void Pending_assignment_is_retained_even_when_old()
        => Assert.True(WorkDayClientRules.ShouldRetainAssignment(
            DateTimeOffset.UtcNow.AddDays(-60),
            DateTimeOffset.UtcNow,
            hasPendingOperation: true,
            hasPendingUpload: false,
            TimeSpan.FromDays(14)));

    [Fact]
    public void Failed_upload_with_local_file_retries()
        => Assert.Equal(LocalUploadState.Retrying, WorkDayClientRules.OnUploadTransportFailure(true));

    [Fact]
    public void Missing_local_file_cannot_be_reported_safe()
        => Assert.False(WorkDayClientRules.IsUploadSafeOnDevice(LocalUploadState.Failed, false));

    [Fact]
    public void Conflict_is_never_acknowledged()
        => Assert.Equal(LocalSyncState.Conflict, WorkDayClientRules.OnServerResult(conflict: true, acknowledged: false));

    [Theory]
    [InlineData("Disabled", true, false)]
    [InlineData("SignInOutOnly", true, false)]
    [InlineData("BackgroundDuringWork", false, false)]
    [InlineData("BackgroundDuringWork", true, true)]
    public void Background_tracking_obeys_policy_and_active_work_session(string policy, bool active, bool expected)
        => Assert.Equal(expected, WorkDayClientRules.ShouldTrackLocation(policy, active));

    [Fact]
    public void Stale_telemetry_is_detected()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.True(WorkDayClientRules.IsTelemetryStale(now.AddMinutes(-10), now, TimeSpan.FromMinutes(5)));
        Assert.False(WorkDayClientRules.IsTelemetryStale(now.AddMinutes(-1), now, TimeSpan.FromMinutes(5)));
    }
}
