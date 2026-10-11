using Aegis.Studio.Web.Workflow;
using Xunit;
namespace Aegis.Studio.Web.Tests;

public sealed class OwnerAssignmentTests
{
    [Theory]
    [InlineData("owner","owner","Assigned",true)]
    [InlineData("OWNER","owner","Accepted",true)]
    [InlineData("manager","owner","Assigned",false)]
    [InlineData("owner","owner","Superseded",false)]
    [InlineData("owner","owner","Declined",false)]
    public void ManagerRoleCannotAcceptAnotherPersonsOwnership(string actor,string assignedTo,string status,bool expected)
        =>Assert.Equal(expected,OwnerAssignmentRules.CanRespond(actor,assignedTo,status));
}
