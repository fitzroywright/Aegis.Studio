using Aegis.Pos.Core;
using Aegis.Studio.Web.Pos;
using Aegis.Studio.Web.Production;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.FrontDesk;
using Npgsql;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public class CommercialProductionTests
{
    private static CommercialTemplate Framed() => new("Framed 8×10", "Prepare, print, frame and inspect", [
        new("prepare", "Prepare approved image", "Edit", RequiresApprovedImage:true),
        new("print", "Print 8×10", "Print", "Printing", ["prepare"], EstimatedCost:100),
        new("frame", "Frame print", "Other", "Framing", ["print"], EstimatedCost:200),
        new("qc", "Inspect finished frame", "QC", "Quality Control", ["frame"], QualityControl:true)]);
    [Fact] public void UnauthorizedDiscountsAndOverridesAreRejected()
    {
        Assert.Equal(100, CommercialRules.Price(100,100,0,false));
        Assert.Throws<UnauthorizedAccessException>(()=>CommercialRules.Price(100,90,0,false));
        Assert.Throws<UnauthorizedAccessException>(()=>CommercialRules.Price(100,null,10,false));
        Assert.Equal(72,CommercialRules.Price(100,80,10,true));
        Assert.Throws<ArgumentException>(()=>CommercialRules.Price(100,null,101,true));
    }
    [Fact] public void WorkOrderTemplatesRequireFinalMandatoryQualityControl()
    {
        CommercialRules.Validate(Framed(),"WorkOrder");
        Assert.Throws<ArgumentException>(()=>CommercialRules.Validate(Framed() with{Steps=Framed().Steps[..3]},"WorkOrder"));
        Assert.Throws<ArgumentException>(()=>CommercialRules.Validate(Framed() with{Steps=[new("qc","Inspect",QualityControl:true),new("print","Print")]},"WorkOrder"));
    }
    [Fact] public void DependenciesRejectCyclesAndForwardReferences()
    {
        Assert.Throws<ArgumentException>(()=>CommercialRules.Validate(new("Cycle","",[new("first","First",DependsOn:["second"]),new("second","Second",DependsOn:["first"],QualityControl:true)]),"WorkOrder"));
        Assert.False(CommercialRules.Ready(["Completed","Blocked"]));Assert.True(CommercialRules.Ready(["Completed","Cancelled"]));
    }
    [Fact] public void PosDefinesSpecificOperationsOnly()
    {
        Assert.Contains("RecordDeposit",FrontDeskPosLaunch.Actions);Assert.Contains("IssueRefund",FrontDeskPosLaunch.Actions);
        Assert.DoesNotContain("OpenPos",FrontDeskPosLaunch.Actions);
        Guid job=Guid.NewGuid(),booking=Guid.NewGuid();var launch=new PosLaunch(Guid.NewGuid(),"NewSale",job,null,null,"Cashier");
        Assert.True(FrontDeskPosLaunch.Matches(launch,$"/api/jobs/{job:D}/pos/sales"));
        Assert.True(FrontDeskPosLaunch.Matches(launch,$"/API/JOBS/{job:D}/POS/SALES"));
        Assert.False(FrontDeskPosLaunch.Matches(launch,$"/api/jobs/{Guid.NewGuid():D}/pos/sales"));
        Assert.False(FrontDeskPosLaunch.Matches(launch,"/api/pos/register/open"));
        Assert.True(FrontDeskPosLaunch.Matches(launch with{Action="RecordDeposit",BookingId=booking},$"/api/frontdesk/booking-requests/{booking:D}/deposits"));
    }
    [StudioPostgresFact] public void CatalogToJobToSaleToProductionUsesImmutableVersionsAndOneWorkOrder()
    {
        using var context=new IntegratedWorkflowTests.WorkflowContext();var db=context.Database;var c=context.Connection;
        // Both application migrations must tolerate restarts.
        CommercialSchema.Ensure(db);CommercialSchema.Ensure(db);
        Sql(c,"alter table studio_jobs add column if not exists workflow_revision bigint not null default 0");
        var templates=new CommercialTemplateStore(db);var original=templates.Save(null,"WorkOrder",Framed(),0,true,"Admin");
        var catalog=new PosCatalogStore(db);var product=catalog.Save(new("FRAME-8X10","Framed photograph","Frames",1000,EstimatedCost:300,RequiresProduction:true,WorkOrderTemplateId:original.Id));
        var jobTemplate=templates.Save(null,"Job",new("Printing Only","In-house framing",[new("review","Review order")],[new(product.Id,3,"8×10 / wood")]),0,true,"Admin");
        var bookingStore=new FrontDeskBookingRequestStore(db);bookingStore.EnsureSchema();
        var booking=bookingStore.Create(new(context.Workspace,"Client","client@example.test","Framed portraits",DateOnly.FromDateTime(DateTime.Today.AddDays(1)),0,"JMD",null,JobTemplateId:jobTemplate.Id),"Admin");
        Guid job=booking.JobId!.Value;
        Assert.Equal(1,Scalar(c,"select count(*)::int from studio_job_activities where job_id=$1",job));
        templates.Save(jobTemplate.Id,"Job",jobTemplate.Definition with{Name="Changed future jobs",Steps=[new("future","Future task")]},jobTemplate.Version,true,"Admin");
        Assert.Equal("Review order",Scalar(c,"select title from studio_job_activities where job_id=$1",job));
        using(var tx=c.BeginTransaction()){CatalogWorkOrders.ConfirmAgreements(c,tx,job);tx.Commit();}
        // Price changes after confirmation must not change the agreed selling price.
        catalog.Save(new(product.Code,product.Name,product.Category,2000,RequiresProduction:true,WorkOrderTemplateId:original.Id),product.Id);
        var pos=new StudioPosStore(db,new PosAuditStore(db));var studio=new StudioStore(db,new WorkspaceStore(db));
        Guid agreement=(Guid)Scalar(c,"select id from studio_job_catalog_agreements where job_id=$1",job)!;
        Guid operation=Guid.NewGuid();var request=new CreateStudioSaleRequest([new(product.Code,product.Name,1000,3,CatalogItemId:product.Id,Specifications:"8×10 / wood",AgreementId:agreement)],operation);
        var sale=pos.Create(job,request,studio,"Admin");Assert.Equal(sale.Id,pos.Create(job,request,studio,"Admin").Id);
        templates.Save(original.Id,"WorkOrder",Framed() with{Name="Future frame",Steps=Framed().Steps.Select(s=>s with{Title="Future "+s.Title}).ToArray()},1,true,"Admin");
        pos.Complete(sale.Id,new([new(TenderKind.Cash,3000,null)],"Test"),studio,"Admin");
        pos.Complete(sale.Id,new([new(TenderKind.Cash,3000,null)],"Test"),studio,"Admin");
        Assert.Equal(sale.Lines.Single().Id,Scalar(c,"select sale_line_id from studio_job_catalog_agreements where id=$1",agreement));
        Assert.Throws<ArgumentException>(()=>pos.Create(job,request with{ClientRequestId=Guid.NewGuid()},studio,"Admin",allowPriceOverride:true));
        Assert.Equal(1,Scalar(c,"select count(*)::int from studio_catalog_work_orders where job_id=$1",job));
        Assert.Equal(1,Scalar(c,"select template_version from studio_catalog_work_orders where job_id=$1",job));
        Assert.Equal(3,Scalar(c,"select quantity from studio_catalog_work_orders where job_id=$1",job));
        Guid order=(Guid)Scalar(c,"select id from studio_catalog_work_orders where job_id=$1",job)!;
        Guid prepare=(Guid)Scalar(c,"select id from studio_job_activities where work_order_id=$1 and template_step_key='prepare'",order)!;
        Guid print=(Guid)Scalar(c,"select id from studio_job_activities where work_order_id=$1 and template_step_key='print'",order)!;
        Assert.Throws<ArgumentException>(()=>CommercialTemplateStore.GuardTask(c,null,prepare,"InProgress"));
        Assert.Throws<ArgumentException>(()=>CommercialTemplateStore.GuardTask(c,null,print,"Completed"));
        var version=context.Versions.Add(job,"frame-source","portrait.jpg","image/jpeg",File.OpenRead(Path.Combine(AppContext.BaseDirectory,"Fixtures","portrait.jpg")),"Editor");
        Sql(c,"insert into studio_media_galleries(id) values($1) on conflict do nothing",job);
        Guid asset=(Guid)Scalar(c,"select id from studio_media_assets where job_id=$1 and asset_key='frame-source'",job)!;Sql(c,"update studio_media_assets set approved_version_id=$2 where id=$1",asset,version.Id);
        Sql(c,"update studio_catalog_work_orders set asset_id=$2,source_version_id=$3 where id=$1",order,asset,version.Id);
        Assert.Throws<ArgumentException>(()=>CommercialTemplateStore.GuardPublication(c,null,job));
        var operations=new OperationalWorkStore(db);
        foreach(var key in new[]{"prepare","print","frame","qc"})
        {
            Guid task=(Guid)Scalar(c,"select id from studio_job_activities where work_order_id=$1 and template_step_key=$2",order,key)!;
            var step=Framed().Steps.Single(s=>s.Key==key);
            var activity=operations.UpdateActivity(task,new(step.ActivityType,step.Title,null,"InProgress","High","Operator",null,DateTimeOffset.UtcNow.AddDays(1),step.Area,null,null),"Admin");
            Assert.Equal("Operator",activity.AssignedTo);Assert.Equal("InProgress",activity.Status);
            activity=operations.UpdateActivity(task,new(step.ActivityType,step.Title,null,"Completed","High","Operator",null,activity.DueAtUtc,step.Area,null,null),"Admin");
            Assert.NotNull(activity.CompletedAtUtc);
        }
        // The job-template review is a real required workflow task, separate from work-order QC.
        Sql(c,"update studio_job_activities set status='Completed',completed_at_utc=now() where job_id=$1 and workflow_node_id='review'",job);
        CommercialTemplateStore.GuardPublication(c,null,job);
        Assert.Equal(4,Scalar(c,"select count(*)::int from studio_job_activities where work_order_id=$1 and status='Completed'",order));
        Assert.Equal(3000m,pos.Get(sale.Id)!.Total);
        Guid refund=Guid.NewGuid();var refundRequest=new RefundStudioSaleRequest([new(sale.Lines.Single().Id,1000,1)],[new(TenderKind.Cash,1000,null)],"Cancel one frame",refund);
        pos.Refund(sale.Id,refundRequest,studio,"Admin");pos.Refund(sale.Id,refundRequest,studio,"Admin");
        Assert.Single(pos.Get(sale.Id)!.Refunds);Assert.Equal(2,Scalar(c,"select quantity from studio_catalog_work_orders where id=$1",order));
        Assert.NotNull(Scalar(c,"select exception_reason from studio_catalog_work_orders where id=$1",order));
        Assert.Equal(4,Scalar(c,"select count(*)::int from studio_job_activities where work_order_id=$1",order));
        Assert.Throws<ArgumentException>(()=>CommercialTemplateStore.GuardPublication(c,null,job));
    }
    [StudioPostgresFact] public void BundlesPreserveQuantitiesAndNonApplicableVariationsAndRefundHistory()
    {
        using var context=new IntegratedWorkflowTests.WorkflowContext();var db=context.Database;var c=context.Connection;
        CommercialSchema.Ensure(db);Sql(c,"alter table studio_jobs add column if not exists workflow_revision bigint not null default 0");
        var templates=new CommercialTemplateStore(db);
        var template=templates.Save(null,"WorkOrder",new("Print and inspect","",[
            new("print","Print","Print","Printing"),new("metal","Assemble metal frame","Other","Assembly",["print"],Optional:true,Variation:"Frame: metal"),
            new("qc","Inspect","QC","Quality Control",["print"],QualityControl:true)]),0,true,"Admin");
        var catalog=new PosCatalogStore(db);
        var print=catalog.Save(new("BUNDLE-PRINT","Print","Prints",50,RequiresProduction:true,WorkOrderTemplateId:template.Id));
        var frame=catalog.Save(new("BUNDLE-FRAME","Frame","Frames",75,RequiresProduction:true,WorkOrderTemplateId:template.Id));
        Assert.Throws<InvalidOperationException>(()=>catalog.Save(new("DUP-PACK","Invalid duplicate package","Packages",100,Bundle:[new(print.Id,1),new(print.Id,2)])));
        var bundle=catalog.Save(new("FRAME-PACK","Print package","Packages",100,TaxRate:10,Bundle:[new(print.Id,1),new(frame.Id,2)]));
        var pos=new StudioPosStore(db,new PosAuditStore(db));var studio=new StudioStore(db,new WorkspaceStore(db));
        var sale=pos.Create(context.Job,new([new(bundle.Code,bundle.Name,110,3,CatalogItemId:bundle.Id,Specifications:"Frame: wood")],Guid.NewGuid()),studio,"Cashier");
        pos.Complete(sale.Id,new([new(TenderKind.Cash,330,null)],"Test"),studio,"Cashier");
        Assert.Equal(2,Scalar(c,"select count(*)::int from studio_catalog_work_orders where job_id=$1",context.Job));
        Assert.Equal(3,Scalar(c,"select quantity from studio_catalog_work_orders where catalog_item_id=$1",print.Id));
        Assert.Equal(6,Scalar(c,"select quantity from studio_catalog_work_orders where catalog_item_id=$1",frame.Id));
        Assert.Equal(2,Scalar(c,"select count(*)::int from studio_job_activities where template_step_key='metal' and status='Cancelled'"));
        Guid variation=(Guid)Scalar(c,"select id from studio_job_activities where template_step_key='metal' limit 1")!;
        Sql(c,"update studio_job_activities set blocked_reason=null where id=$1",variation);
        Assert.Throws<ArgumentException>(()=>CommercialTemplateStore.GuardTask(c,null,variation,"InProgress"));
        pos.Refund(sale.Id,new([new(sale.Lines.Single().Id,110,3)],[new(TenderKind.Cash,330,null)],"Cancel unstarted bundle",Guid.NewGuid()),studio,"Cashier");
        Assert.Equal(0,Scalar(c,"select sum(quantity)::int from studio_catalog_work_orders where job_id=$1",context.Job));
        Assert.Equal(6,Scalar(c,"select count(*)::int from studio_job_activities where work_order_id is not null and status='Cancelled'"));
    }
    private static object? Scalar(NpgsqlConnection c,string sql,params object[] values){using var q=new NpgsqlCommand(sql,c);foreach(var v in values)q.Parameters.AddWithValue(v);return q.ExecuteScalar();}
    private static void Sql(NpgsqlConnection c,string sql,params object[] values){using var q=new NpgsqlCommand(sql,c);foreach(var v in values)q.Parameters.AddWithValue(v);q.ExecuteNonQuery();}
}
