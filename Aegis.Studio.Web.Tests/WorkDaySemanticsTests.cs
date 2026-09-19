using Aegis.Studio.Web;
using Aegis.Studio.Web.WorkDay;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class WorkDaySemanticsTests
{
    [Theory]
    [InlineData("Assigned","Accepted",true)]
    [InlineData("Assigned","Arrived",true)]
    [InlineData("Accepted","Arrived",true)]
    [InlineData("Arrived","InProgress",true)]
    [InlineData("InProgress","Completed",true)]
    [InlineData("Assigned","Completed",false)]
    [InlineData("Completed","Arrived",false)]
    [InlineData("Cancelled","InProgress",false)]
    public void Session_transition_rules_are_deliberate(string current,string next,bool expected)
        => Assert.Equal(expected,WorkDaySemantics.CanTransition(current,next));

    [Theory]
    [InlineData("Cancelled")]
    [InlineData("NoShow")]
    [InlineData("Delayed")]
    [InlineData("UnableToComplete")]
    public void Exception_statuses_are_normalized(string value)
        => Assert.Equal(value,WorkDaySemantics.NormalizeExceptionStatus(value));

    [Fact]
    public void Unknown_exception_status_fails_closed()
        => Assert.Null(WorkDaySemantics.NormalizeExceptionStatus("Whatever"));

    [Fact]
    public void WorkDay_flow_definitions_are_real_and_ordered()
    {
        Assert.Equal(["Assigned","Accepted","Arrived","InProgress","Completed","Synchronized"],StudioFlowSemantics.StageNames("WorkDaySession"));
        Assert.Equal(["LocalChange","Pending","Sending","ServerProcessing","Acknowledged"],StudioFlowSemantics.StageNames("WorkDaySync"));
        Assert.Equal(["Selected","LocalQueue","Upload","Storage","MetadataAssociation","Acknowledged"],StudioFlowSemantics.StageNames("WorkDayUpload"));
    }

    [Fact]
    public void WorkDay_sync_unknown_is_not_reported_completed()
    {
        DateTimeOffset now=DateTimeOffset.UtcNow;
        IReadOnlyList<StudioFlowStageSnapshot> stages=StudioFlowSemantics.Linear("WorkDaySync","Sending",now,false,"No acknowledgement","Unknown");
        StudioFlowStageSnapshot sending=Assert.Single(stages.Where(x=>x.Name=="Sending"));
        Assert.Equal("Unknown",sending.State);
        Assert.Null(sending.CompletedAtUtc);
    }
}
