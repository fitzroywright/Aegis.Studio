using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Production;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class NextActionRulesTests
{
    private static NextActionFacts Facts(string stage="Pending") => new(stage,true,"owner","Accepted",0,true,16,0,null,null,[]);
    [Theory]
    [InlineData("Closed")][InlineData("Completed")][InlineData("Cancelled")][InlineData("Archived")]
    public void ClosedJobsHaveNoAction(string stage)=>Assert.Null(NextActionRules.Job(Facts(stage)));
    [Fact] public void DepositPrecedesConfirmationAndUsesRecordedAmounts()
    {
        Assert.Equal("collect-deposit",NextActionRules.Job(Facts() with{DepositOutstanding=100})!.Key);
        Assert.Equal("confirm-booking",NextActionRules.Job(Facts())!.Key);
    }
    [Fact] public void ClientAndOwnershipPrerequisitesAreVisible()
    {
        Assert.Equal("match-client",NextActionRules.Job(Facts() with{HasClient=false})!.Key);
        Assert.Equal("assign-owner",NextActionRules.Job(Facts("Confirmed") with{Owner=null})!.Key);
        var accepted=NextActionRules.Job(Facts("Confirmed") with{OwnerStatus="Assigned"})!;
        Assert.Equal("accept-owner",accepted.Key);Assert.Equal("owner",accepted.AssignedTo);
        Assert.Equal("session-plan",NextActionRules.Job(Facts("Confirmed") with{Subjects=0})!.Key);
    }
    [Theory]
    [InlineData("Needs Photographer Assignment","assign-photographer")]
    [InlineData("Awaiting Scheduling","schedule")]
    [InlineData("Scheduled","shoot")]
    [InlineData("Awaiting Production Review","review")]
    [InlineData("Awaiting Approval","approve")]
    [InlineData("Ready for Delivery","deliver")]
    [InlineData("Delivered","close")]
    public void LifecycleActionOpensExistingControls(string stage,string expected)
    {var action=NextActionRules.Job(Facts(stage))!;Assert.Equal(expected,action.Key);Assert.Equal("workflow",action.Route);Assert.NotNull(action.Selector);}
    [Fact] public void UploadedUnclassifiedMediaOpensTheMediaInsteadOfAdvancingTheJob()
    {
        var action=NextActionRules.Job(Facts("Awaiting Upload") with{Unclassified=23})!;
        Assert.Equal("classify-media",action.Key);Assert.Equal("media",action.Route);Assert.Contains("23",action.Reason);
        Assert.Equal("upload",NextActionRules.Job(Facts("Awaiting Upload"))!.Key);
    }
    [Fact] public void NestedGraphTasksAndTheirAssigneeAreUsedForProduction()
    {
        Guid id=Guid.NewGuid();var task=new GraphTask(id,"nested/edit","Retouch photographs","NotStarted","editor","Editor","Available",[],false,null);
        var action=NextActionRules.Job(Facts("Active Production") with{Tasks=[task]})!;
        Assert.Equal("task:nested/edit",action.Key);Assert.Equal(id,action.TaskId);Assert.Equal("editor",action.AssignedTo);
    }
    [Fact] public void InactiveOrCompletedGraphTasksAreNotNextAndBlockingReasonsSurvive()
    {
        var task=new GraphTask(Guid.NewGuid(),"inactive","Excluded service","NotStarted",null,null,"Inactive",[],false,null);
        Assert.Equal("submit-finished",NextActionRules.Job(Facts("Active Production") with{Tasks=[task]})!.Key);
        var blocked=NextActionRules.Job(Facts() with{BlockedReason="Client has requested a hold"})!;
        Assert.Equal("resolve-blocker",blocked.Key);Assert.Equal("Client has requested a hold",blocked.Reason);
    }
    [Fact] public void EnquiriesProgressFromMatchingAndReplyToQuotationAndAcceptance()
    {
        Assert.Null(NextActionRules.Enquiry(true,true,true,null));
        Assert.Equal("match-client",NextActionRules.Enquiry(false,false,true,null)!.Key);
        Assert.Equal("reply",NextActionRules.Enquiry(false,true,true,"Accepted")!.Key);
        Assert.Equal("requirements",NextActionRules.Enquiry(false,true,false,null)!.Key);
        Assert.Equal("send-quote",NextActionRules.Enquiry(false,true,false,"Draft")!.Key);
        Assert.Equal("follow-up",NextActionRules.Enquiry(false,true,false,"Queued")!.Key);
        Assert.Equal("prepare-booking",NextActionRules.Enquiry(false,true,false,"Accepted")!.Key);
    }
}
