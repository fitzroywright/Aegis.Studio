using Aegis.Studio.WorkDay.Core;
using Aegis.Studio.Web.WorkDay;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class PhotographerOfflineRulesTests
{
    [Fact]
    public void FourOfflineReportsHaveConsecutiveVersionsAndPreserveServerReplayVersion()
    {
        List<long> queued=[];
        foreach(string kind in new[]{"AcceptAssignment","Arrive","StartSession","CompleteSession"})
        {
            Assert.True(WorkDayClientRules.ChangesAssignmentStatus(kind));
            queued.Add(WorkDayClientRules.NextExpectedVersionAfterPending(0,queued));
        }
        Assert.Equal(new long[]{0,1,2,3},queued);
        // Bootstrap already saw a lost acknowledgement: pending replay still uses its original version.
        Assert.Equal(4,WorkDayClientRules.NextExpectedVersionAfterPending(1,queued));
        Assert.Equal(7,WorkDayClientRules.NextExpectedVersionAfterPending(7,[]));
    }

    [Theory]
    [InlineData("Acknowledged",true)]
    [InlineData("Failed",false)]
    [InlineData("Conflict",false)]
    [InlineData("Pending",false)]
    [InlineData(null,false)]
    public void OnlyExplicitAcknowledgementsMayDiscardPendingWork(string? state,bool expected)
        =>Assert.Equal(expected,WorkDayClientRules.IsAcknowledged(state));

    [Theory]
    [InlineData(WorkDayOperationKind.WorkSignIn)]
    [InlineData(WorkDayOperationKind.WorkSignOut)]
    [InlineData((WorkDayOperationKind)999)]
    public void PhotographerReportingExcludesTimekeepingAndUnknownOperations(WorkDayOperationKind kind)
        =>Assert.False(WorkDaySemantics.IsPhotographerOperation(kind));

    [Fact]
    public void DeclineIsAnAssignmentResponseAndNotesDoNotConsumeAVersion()
    {
        Assert.True(WorkDaySemantics.IsPhotographerOperation(WorkDayOperationKind.DeclineAssignment));
        Assert.True(WorkDayClientRules.ChangesAssignmentStatus("DeclineAssignment"));
        Assert.False(WorkDayClientRules.ChangesAssignmentStatus("AddNote"));
        Assert.Equal("Declined",WorkDayClientRules.AssignmentStatus("DeclineAssignment",null,"Assigned"));
    }
}
