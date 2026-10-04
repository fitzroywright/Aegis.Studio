using Aegis.Studio.Web.Operations;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class JobLifecycleRulesTests
{
    [Theory]
    [InlineData("Awaiting Upload","submit-review","Needs Review")]
    [InlineData("Needs Review","request-changes","Changes Requested")]
    [InlineData("Changes Requested","submit-review","Needs Review")]
    [InlineData("Needs Review","approve","In Production")]
    [InlineData("In Production","finish-production","Ready for Client")]
    [InlineData("Ready for Client","publish","Published")]
    public void BryansWorkflowSupportsReviewAndResubmission(string status,string action,string expected)
    {
        JobLifecycleRules.Validate(status,action,1,false,true);
        Assert.Equal(expected,JobLifecycleRules.Target(action));
    }
    [Theory]
    [InlineData("Needs Scheduling","approve")]
    [InlineData("Published","submit-review")]
    [InlineData("Cancelled","finish-production")]
    [InlineData("Awaiting Upload","publish")]
    public void CannotSkipRequiredStages(string status,string action)=>Assert.Throws<ArgumentException>(()=>JobLifecycleRules.Validate(status,action,1,false,true));
    [Fact]
    public void SubmissionRequiresMedia()=>Assert.Throws<ArgumentException>(()=>JobLifecycleRules.Validate("Awaiting Upload","submit-review",0,false,true));
    [Fact]
    public void ProductionRequiresCompletedTasks()=>Assert.Throws<ArgumentException>(()=>JobLifecycleRules.Validate("In Production","finish-production",1,true,true));
    [Fact]
    public void PublicationRequiresConfiguredProvider()=>Assert.Throws<ArgumentException>(()=>JobLifecycleRules.Validate("Ready for Client","publish",1,false,false));
}
