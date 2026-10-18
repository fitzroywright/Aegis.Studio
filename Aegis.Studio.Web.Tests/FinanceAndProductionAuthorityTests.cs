using Aegis.Studio.Web.Finance;
using Aegis.Studio.Web.Production;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class FinanceAndProductionAuthorityTests
{
    [Theory]
    [InlineData(100, 100, false, true, true)]
    [InlineData(100, 99, false, true, false)]
    [InlineData(100, 200, true, true, false)]
    [InlineData(100, 200, false, false, false)]
    [InlineData(0, 200, false, true, false)]
    public void FinanceAuthorityMustCoverTheActualReceivable(decimal balance, decimal amount, bool revoked, bool sameClient, bool expected)
        => Assert.Equal(expected, FinanceReleaseRules.Covers(balance, amount, revoked, sameClient));

    [Fact]
    public void CreditRequiresDueDateTermsAndContractWithoutCreatingPayment()
    {
        var today = new DateOnly(2026, 10, 11);
        var valid = new FinanceAuthorizationRequest(1500, "CreditTerms", "Net 30", today.AddDays(30), "Contract 42", "Approved contract");
        FinanceReleaseRules.Validate(valid, today);
        Assert.Throws<ArgumentException>(() => FinanceReleaseRules.Validate(valid with { DueDate = today.AddDays(-1) }, today));
        Assert.Throws<ArgumentException>(() => FinanceReleaseRules.Validate(valid with { Amount = 1500.001m }, today));
        Assert.Throws<ArgumentException>(() => FinanceReleaseRules.Validate(valid with { Reference = "" }, today));
        Assert.Throws<ArgumentException>(() => FinanceReleaseRules.Validate(valid with { Kind = "OperationsOverride" }, today));
    }

    [Theory]
    [InlineData(false, true, "Edit", "Edit", false)]
    [InlineData(false, true, "Edit", "Other", false)]
    [InlineData(false, true, "Other", "Print", false)]
    [InlineData(false, true, "Other", "Other", true)]
    [InlineData(true, true, "Edit", "Edit", true)]
    [InlineData(true, false, "QC", "QC", true)]
    public void OwnershipDoesNotConferProductionDispatch(bool productionManager, bool owner, string existing, string requested, bool expected)
        => Assert.Equal(expected, ProductionAuthorityRules.CanDispatch(productionManager, owner, existing, requested));

    [Theory]
    [InlineData(false,"Pending",true)]
    [InlineData(false,"Passed",false)]
    [InlineData(false,"Rejected",false)]
    [InlineData(true,"Passed",true)]
    [InlineData(true,"Rejected",true)]
    public void AssignedStaffProgressDoesNotConferTechnicalApproval(bool manager,string state,bool expected)
        =>Assert.Equal(expected,ProductionAuthorityRules.CanDecideQuality(manager,state));

    [Theory]
    [InlineData(false, true, false, "Edit", false)]
    [InlineData(false, true, true, "Edit", true)]
    [InlineData(true, true, false, "Edit", true)]
    [InlineData(false, true, false, "Delivery", true)]
    public void ProductionProgressBelongsToAssigneeOrProductionManager(bool productionManager, bool owner, bool assigned, string kind, bool expected)
        => Assert.Equal(expected, ProductionAuthorityRules.CanReport(productionManager, owner, assigned, kind));
}
