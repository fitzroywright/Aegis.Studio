using System.Reflection;
using Aegis.Studio.Web.Operations;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class DemoImageTests
{
    [Fact]
    public void EveryDemoGalleryUsesTheLocalPhotoLibrary()
    {
        Type seed=typeof(DemoDataEndpoints).Assembly.GetType("Aegis.Studio.Web.Operations.DemoFullStudioData")!;
        var jobs=(System.Collections.IEnumerable)seed.GetField("Jobs",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
        var count=seed.GetMethod("MediaCount",BindingFlags.NonPublic|BindingFlags.Static)!;
        var source=seed.GetMethod("PortraitSource",BindingFlags.NonPublic|BindingFlags.Static)!;
        foreach(var job in jobs)
        {
            Assert.True((int)count.Invoke(null,new[]{job})! >= 40);
            Assert.Null(source.Invoke(null,new object[]{job,0}));
        }
    }

    [Fact]
    public void DemoPhotoCategoriesMatchTheInstalledLocalLibrary()
    {
        Type seed=typeof(DemoDataEndpoints).Assembly.GetType("Aegis.Studio.Web.Operations.DemoDataExpansion")!;
        MethodInfo category=seed.GetMethod("PhotoCategory",BindingFlags.NonPublic|BindingFlags.Static)!;
        Assert.Equal("people",category.Invoke(null,new object?[]{"Corporate Headshots"}));
        Assert.Equal("people",category.Invoke(null,new object?[]{"Passport / Visa"}));
        Assert.Equal("people",category.Invoke(null,new object?[]{"Portrait"}));
        Assert.Equal("wedding",category.Invoke(null,new object?[]{"Wedding"}));
        Assert.Equal("wedding",category.Invoke(null,new object?[]{"Engagement"}));
        Assert.Equal("graduation",category.Invoke(null,new object?[]{"Graduation"}));
        Assert.Equal("family",category.Invoke(null,new object?[]{"Newborn"}));
        Assert.Equal("family",category.Invoke(null,new object?[]{"Family Portrait"}));
        Assert.Equal("sports",category.Invoke(null,new object?[]{"Sports Team"}));
        Assert.Equal("product",category.Invoke(null,new object?[]{"Commercial Campaign"}));
    }
    [Fact]
    public void DemoWorkerRoleStatusRulesFollowJobStages()
    {
        Type seed=typeof(DemoDataEndpoints).Assembly.GetType("Aegis.Studio.Web.Operations.DemoFullStudioData")!;
        Type jobType=seed.GetNestedType("DemoJob",BindingFlags.NonPublic)!;
        Type[] jobSignature=[typeof(int),typeof(int),typeof(int),typeof(string),typeof(string),typeof(int),typeof(string),typeof(decimal),typeof(decimal),typeof(string),typeof(string)];
        ConstructorInfo constructor=jobType.GetConstructors(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)
            .Single(candidate=>candidate.GetParameters().Select(parameter=>parameter.ParameterType).SequenceEqual(jobSignature));
        object Job(string status,int dayOffset=0)=>constructor.Invoke(new object[]{101,1,1,"Demo job",status,dayOffset,"Studio A",100m,50m,"demo.sam","Portrait"});
        MethodInfo roleStatus=seed.GetMethod("RoleStatus",BindingFlags.NonPublic|BindingFlags.Static)!;
        string Status(object job,string role,string stage,int ordinal=1)=>(string)roleStatus.Invoke(null,new object[]{job,role,stage,ordinal})!;

        object scheduled=Job("Scheduled",3);
        Assert.Equal("Assigned",Status(scheduled,"PHOTOGRAPHER","Scheduled",1));
        Assert.Equal("Accepted",Status(scheduled,"PHOTOGRAPHER","Scheduled",2));

        object editing=Job("Editing");
        Assert.Equal("Accepted",Status(editing,"EDITOR","Changes Requested"));
        Assert.Equal("Accepted",Status(editing,"RETOUCHER","Changes Requested",2));

        object qualityControl=Job("QC");
        Assert.Equal("Accepted",Status(qualityControl,"REVIEWER","In Production"));

        object completed=Job("Completed",-1);
        Assert.Equal("Completed",Status(completed,"PHOTOGRAPHER","Completed"));
        Assert.Equal("Completed",Status(completed,"DELIVERY","Completed"));

        object cancelled=Job("Cancelled",3);
        Assert.Equal("Cancelled",Status(cancelled,"PHOTOGRAPHER","Cancelled"));
        Assert.Equal("Cancelled",Status(cancelled,"EDITOR","Cancelled"));
    }

    [Fact]
    public void LargeWeddingAndGraduationMediaCannotOverwriteAdjacentJobs()
    {
        Type seed=typeof(DemoDataEndpoints).Assembly.GetType("Aegis.Studio.Web.Operations.DemoFullStudioData")!;
        var allocate=seed.GetMethod("MediaId",BindingFlags.NonPublic|BindingFlags.Static)!;
        var ids=new HashSet<Guid>();
        // The old 100-record stride collided as soon as a wedding exceeded 100 assets.
        foreach(int group in new[]{59,64,65,66})
        foreach(int job in new[]{201,202,203,301,302,303,401,402,403})
        foreach(int asset in Enumerable.Range(0,500))
            Assert.True(ids.Add((Guid)allocate.Invoke(null,new object[]{group,job,asset})!));
    }
}


