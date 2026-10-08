using Aegis.Studio.Web.Workflow;
using Xunit;
namespace Aegis.Studio.Web.Tests;
public class ConnectedWorkflowRulesTests
{
    private static readonly DateTimeOffset Start=DateTimeOffset.Parse("2026-10-09T21:00:00Z");
    [Fact] public void AnyPositiveDepositEnablesExplicitConfirmation()
    {Assert.True(WorkflowRules.DepositEligible(0.01m));Assert.False(WorkflowRules.DepositEligible(0));Assert.Equal("Needs Assignment",WorkflowRules.Target("Pending","confirm"));}
    [Theory][InlineData(1.99,"green")][InlineData(2,"amber")][InlineData(8,"red")]
    public void ElapsedClockUsesExactBoundaries(double hours,string expected)
    {Assert.Equal(expected,WorkflowRules.Timing("Pending",Start,Start.AddHours(hours),new("Pending",2,8)).Light);}
    [Fact] public void WeekendsAreCountedAndUnknownHistoryRemainsUnknown()
    {Assert.Equal("red",WorkflowRules.Timing("Pending",Start,Start.AddDays(2),new("Pending",2,8)).Light);Assert.Equal("neutral",WorkflowRules.Timing("Pending",null,Start,null).Light);}
    [Fact] public void FutureScheduledWorkDoesNotAgeIntoOverdue()
    {Assert.Equal("green",WorkflowRules.Timing("Scheduled",Start.AddDays(-30),Start,null,Start.AddDays(14)).Light);Assert.Equal("red",WorkflowRules.Timing("Scheduled",Start.AddDays(-30),Start.AddHours(4),null,Start).Light);}
    [Fact] public void ClosedDurationsFreezeAndBusinessHoursAreRejected()
    {Assert.Equal(2d,WorkflowRules.Timing("Completed",Start,Start.AddDays(30),null,closed:Start.AddHours(2)).ElapsedHours);Assert.Throws<ArgumentException>(()=>WorkflowRules.ValidateThreshold(new("Pending",2,8,"BusinessHours")));}
    [Fact] public void SourceReviewAndFinishedApprovalAreDistinct()
    {Assert.Equal("Active Production",WorkflowRules.Target("Awaiting Production Review","accept-source"));Assert.Equal("Ready for Delivery",WorkflowRules.Target("Awaiting Approval","approve-finished"));Assert.Throws<ArgumentException>(()=>WorkflowRules.Target("Active Production","approve-finished"));}
}
