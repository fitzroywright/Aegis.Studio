using Aegis.Studio.WorkDay.Core;
using Xunit;
namespace Aegis.Studio.Web.Tests;
public sealed class WorkDayRecoveryRulesTests
{
    [Theory]
    [InlineData("Conflict",1,true)]
    [InlineData("Conflict",5,true)]
    [InlineData("Pending",0,true)]
    [InlineData("Failed",0,true)]
    [InlineData("Failed",1,false)]
    [InlineData("Sending",1,false)]
    [InlineData("Pending",1,false)]
    [InlineData("Acknowledged",1,false)]
    public void RecoveryRetainsUncertainReportsForOriginalOperationReplay(string state,int attempts,bool expected)
        =>Assert.Equal(expected,WorkDayClientRules.CanResolveReport(state,attempts));
    [Theory]
    [InlineData("Resolved",false)]
    [InlineData("Acknowledged",false)]
    [InlineData("Conflict",true)]
    [InlineData("Sending",true)]
    [InlineData("Pending",true)]
    public void ResolutionIsDistinctFromAcknowledgement(string state,bool outstanding)
    {
        Assert.Equal(outstanding,WorkDayClientRules.IsOutstanding(state));
        Assert.Equal(state=="Acknowledged",WorkDayClientRules.IsAcknowledged(state));
    }
}
