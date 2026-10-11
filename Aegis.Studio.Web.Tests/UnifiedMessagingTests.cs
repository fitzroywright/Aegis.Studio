using Aegis.Studio.Web.Messaging;
using Common.Messaging;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class UnifiedMessagingTests
{
    private static StudioAddressEntry Person(string mode="InternalOnly",string email="tamara@studio.test")
        =>new(Guid.NewGuid(),"Tamara",email,"Staff",mode=="InternalOnly"?"Internal":"Microsoft365",mode,"tamara",UserName:"tamara");
    private static IConfiguration Config(bool fanOut=false)=>new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
    { ["Messaging:MicrosoftGraph:Email:SenderUpn"]="router@studio.test",["Messaging:Router:SelectiveFanOutEnabled"]=fanOut.ToString() }).Build();

    [Fact]
    public async Task InternalOnlyNeverCallsProviderDiscovery()
    {
        var directory=new Directory();var result=await new StudioDeliveryPlanner(directory,Config()).PlanAsync([Person()]);
        Assert.Empty(result.ExternalRecipients);Assert.Empty(result.Attention);Assert.Equal(0,directory.Calls);
    }
    [Theory]
    [InlineData("ExternalCopy",1,0)]
    [InlineData("InternalOnly",0,0)]
    [InlineData("Hold",0,1)]
    public async Task ConventionalCombinedMailboxHonorsExplicitPolicy(string policy,int copies,int attention)
    {
        var result=await new StudioDeliveryPlanner(new Directory(),Config()).PlanAsync([Person("Combined") with{CombinedMailboxPolicy=policy}]);
        Assert.Equal(copies,result.ExternalRecipients.Count);Assert.Equal(attention,result.Attention.Count);
    }
    [Fact]
    public async Task VerifiedGroupsExcludeRouterAndDeduplicateOverlappingMembersAndDirectRecipients()
    {
        var a=Person("Combined") with{StudioManagedGroup=true,GroupVerified=true,ProviderReference="a"};
        var b=Person("Combined","lisa@studio.test") with{Id=Guid.NewGuid(),StudioManagedGroup=true,GroupVerified=true,ProviderReference="b"};
        var directory=new Directory();directory.Groups["a"]=new("a",a.EmailAddress,true,["ROUTER@studio.test","client@outside.test","shared@outside.test"]);
        directory.Groups["b"]=new("b",b.EmailAddress,true,["shared@outside.test","router@studio.test"]);
        var direct=new StudioAddressEntry(Guid.NewGuid(),"Client","CLIENT@outside.test","Client","External","ExternalOnly");
        var result=await new StudioDeliveryPlanner(directory,Config(true)).PlanAsync([a,b,direct]);
        Assert.Equal(new[]{"client@outside.test","shared@outside.test"},result.ExternalRecipients);Assert.Equal(2,result.Exclusions.Count);Assert.Empty(result.Attention);
    }
    [Fact]
    public async Task UnverifiedGroupHoldsAllExternalCopiesWithoutPartialFanOut()
    {
        var directory=new Directory();var group=Person("Combined") with{StudioManagedGroup=true,ProviderReference="a"};
        var result=await new StudioDeliveryPlanner(directory,Config(true)).PlanAsync([group,Person("ExternalOnly","client@outside.test")]);
        Assert.Empty(result.ExternalRecipients);Assert.Single(result.Attention);Assert.Equal(0,directory.Calls);
    }
    [Fact]
    public async Task UnsupportedGroupIsVisibleAndDoesNotDiscardOrGuessRecipients()
    {
        var group=Person("Combined") with{StudioManagedGroup=true,GroupVerified=true,ProviderReference="a"};
        var directory=new Directory();directory.Groups["a"]=new("a",group.EmailAddress,false,[],"Nested group requires review.");
        var result=await new StudioDeliveryPlanner(directory,Config(true)).PlanAsync([group]);
        Assert.Empty(result.ExternalRecipients);Assert.Contains("Nested",Assert.Single(result.Attention).Reason);
    }
    [Fact]
    public async Task UnregisteredOrganizationalGroupUsesNormalProviderDelivery()
    {
        var directory=new Directory();var result=await new StudioDeliveryPlanner(directory,Config(true)).PlanAsync([Person("ExternalOnly","ordinary@studio.test")]);
        Assert.Equal("ordinary@studio.test",Assert.Single(result.ExternalRecipients));Assert.Equal(0,directory.Calls);
    }
    [Fact]
    public async Task MonitoringMailboxCannotReceiveAnOutboundCopy()
    {
        var result=await new StudioDeliveryPlanner(new Directory(),Config()).PlanAsync([Person("ExternalOnly","router@studio.test")]);
        Assert.Empty(result.ExternalRecipients);Assert.Single(result.Attention);
    }
    [Fact]
    public void StandardReplyReferencesIgnoreUnrelatedHeadersAndAreCaseInsensitive()
    {
        var result=StudioUnifiedInbound.ReplyReferences(new Dictionary<string,string>{["in-reply-to"]="<one@studio.test>",["References"]="<prior@studio.test> <one@studio.test>",["X-Studio-Message-Id"]="not-a-reference"});
        Assert.Equal(2,result.Length);Assert.Contains("<one@studio.test>",result);Assert.Contains("<prior@studio.test>",result);
    }
    [Theory]
    [InlineData("not-an-address")]
    [InlineData("Paul <paul@studio.test>")]
    [InlineData("paul@studio.test\r\nBcc: other@outside.test")]
    public void AddressesCannotInjectHeadersOrReplaceAnIdentity(string address)
        =>Assert.Throws<InvalidOperationException>(()=>StudioAddressBook.ValidateAddress(address));

    internal sealed class Directory:IEmailRecipientDirectory
    {
        public int Calls {get;private set;}
        public Dictionary<string,EmailGroupMembership> Groups {get;}=[];
        public Task<EmailGroupMembership> GetGroupAsync(string id,CancellationToken token=default)
        {Calls++;return Task.FromResult(Groups.GetValueOrDefault(id)??new(id,"",false,[],"Missing group"));}
    }
}
