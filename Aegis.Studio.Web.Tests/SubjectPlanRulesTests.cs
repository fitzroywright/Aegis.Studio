using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.SessionPlanning;
using Xunit;
namespace Aegis.Studio.Web.Tests;

public sealed class SubjectPlanRulesTests
{
    [Fact]
    public void ArbitraryCategoriesAndHierarchyLevelsAreSupported()
    {
        var plan = new SubjectPlan(0, "Campaign subjects", [new("Region", "East"), new("Site", "Harbour"), new("Team", "B")], "Spokesperson", false, [new("Profile with product", "Pending", [])]);
        SubjectPlanRules.Validate(plan);
        Assert.Equal(plan.Groups, SubjectPlanRules.ParseHierarchy("Region: East / Site: Harbour / Team: B"));
    }
    [Fact]
    public void UploadedFilesCannotApproveShotsWithoutIdentityAndVersionEvidence()
    {
        var request = new SubjectPlan(0, "People", [], "", false, [new("Portrait", "Approved", [Guid.NewGuid()], Guid.NewGuid())]);
        Assert.Throws<ArgumentException>(() => SubjectPlanRules.Validate(request));
        Assert.Throws<ArgumentException>(() => SubjectPlanRules.Validate(request with { IdentityConfirmed = true, Shots = [new("Portrait", "Approved", [Guid.NewGuid()])] }));
        SubjectPlanRules.Validate(request with { IdentityConfirmed = true });
    }
    [Fact]
    public void DuplicateRequirementsAndMalformedHierarchyAreRejected()
    {
        var request = new SubjectPlan(0, "Products", [], "", false, [new("Front", "Pending", []), new(" front ", "Pending", [])]);
        Assert.Throws<ArgumentException>(() => SubjectPlanRules.Validate(request));
        Assert.Throws<ArgumentException>(() => SubjectPlanRules.ParseHierarchy("Grade 7 / Class A"));
        Assert.Throws<ArgumentException>(() => SubjectPlanRules.ParseHierarchy("Level: A / level: B"));
    }
    [Fact]
    public void LargeSchoolDemoIncludesNestedClassesDepartmentsAndSeparateGroupDeliverables()
    {
        var people = DemoSessionPlanExamples.Build("Sample School Portraits", true);
        var students = people.Where(p => p.Plan.Category == "Students").ToArray();
        Assert.Equal(1200, students.Length);
        Assert.Equal(40, students.Select(p => p.Collection).Distinct().Count());
        Assert.Equal(6, students.Select(p => p.Plan.Groups[0].Value).Distinct().Count());
        Assert.Equal(96, people.Count(p => p.Plan.Category == "Staff"));
        Assert.Equal(48, people.Count(p => p.Plan.Category == "Group photos"));
        Assert.Equal(people.Length, people.Select(p => p.Reference).Distinct().Count());
        Assert.Equal(2, students.Count(p => p.Name == "Amara Williams"));
        foreach (var person in people) SubjectPlanRules.Validate(person.Plan);
    }
    [Theory]
    [InlineData("Family Portraits")]
    [InlineData("Wedding")]
    [InlineData("Corporate Headshots")]
    [InlineData("Product Catalogue")]
    public void OtherDemoJobsHaveAppropriateNonSchoolPlans(string title)
    {
        var people = DemoSessionPlanExamples.Build(title, false);
        Assert.NotEmpty(people);
        Assert.All(people, p => { SubjectPlanRules.Validate(p.Plan); Assert.NotEmpty(p.Plan.Shots); });
        Assert.DoesNotContain(people, p => p.Plan.Groups.Any(g => g.Name == "Grade"));
    }
}
