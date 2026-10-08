using Aegis.Studio.Web.Operations;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class JobLifecycleRulesTests
{
    [Theory]
    [InlineData("Awaiting Upload","submit-review","Awaiting Production Review")]
    [InlineData("Awaiting Production Review","request-changes","Changes Requested")]
    [InlineData("Changes Requested","submit-review","Awaiting Production Review")]
    [InlineData("Awaiting Production Review","approve","Active Production")]
    [InlineData("Active Production","finish-production","Awaiting Approval")]
    [InlineData("Awaiting Approval","approve-finished","Ready for Delivery")]
    [InlineData("Ready for Delivery","publish","Ready for Delivery")]
    public void ConnectedWorkflowSupportsReviewAndResubmission(string status,string action,string expected)
    {
        JobLifecycleRules.Validate(status,action,1,false,true);
        Assert.Equal(expected,JobLifecycleRules.Target(action));
    }

    [Theory]
    [InlineData("Needs Scheduling","approve")]
    [InlineData("Published","submit-review")]
    [InlineData("Cancelled","finish-production")]
    [InlineData("Awaiting Upload","publish")]
    [InlineData("Needs Review","approve")]
    [InlineData("In Production","finish-production")]
    [InlineData("Ready for Client","publish")]
    public void CannotSkipRequiredStages(string status,string action)
        =>Assert.Throws<ArgumentException>(()=>JobLifecycleRules.Validate(status,action,1,false,true));

    [Fact]
    public void SubmissionRequiresMedia()
        =>Assert.Throws<ArgumentException>(()=>JobLifecycleRules.Validate("Awaiting Upload","submit-review",0,false,true));
    [Fact]
    public void ProductionRequiresCompletedTasks()
        =>Assert.Throws<ArgumentException>(()=>JobLifecycleRules.Validate("Active Production","finish-production",1,true,true));
    [Fact]
    public void PublicationRequiresConfiguredProvider()
        =>Assert.Throws<ArgumentException>(()=>JobLifecycleRules.Validate("Ready for Delivery","publish",1,false,false));
}
