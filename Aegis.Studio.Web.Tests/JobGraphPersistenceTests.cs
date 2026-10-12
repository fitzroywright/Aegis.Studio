using Aegis.Studio.Web.Production;
using Aegis.Studio.Web.FrontDesk;
using Aegis.Studio.Web.Pos;
using Aegis.Studio.Web.Operations;
using Aegis.Pos.Core;
using Npgsql;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public class JobGraphPersistenceTests
{
    private static CommercialTemplate Linear(string name="Linear") => JobGraphStore.Normalize(new(name,"",[],Workflow:WorkflowGraphExamples.Linear()));
    private static object? Scalar(NpgsqlConnection c,string sql,params object[] values){using var q=new NpgsqlCommand(sql,c);foreach(var v in values)q.Parameters.AddWithValue(v);return q.ExecuteScalar();}
    private static void Sql(NpgsqlConnection c,string sql,params object[] values){using var q=new NpgsqlCommand(sql,c);foreach(var v in values)q.Parameters.AddWithValue(v);q.ExecuteNonQuery();}
    [StudioPostgresFact] public void DraftPublicationPreservesPublishedVersionsAndJobSnapshots()
    {
        using var ctx=new IntegratedWorkflowTests.WorkflowContext();var drafts=new WorkflowTemplateStore(ctx.Database);Guid id=Guid.NewGuid();
        var d=drafts.SaveDraft(id,new(Linear()),"Admin");Assert.Equal(1,d.Revision);Assert.Empty(new CommercialTemplateStore(ctx.Database).List("Job"));
        var v=drafts.Publish(id,d.Revision,"Admin");Assert.Equal(1,v.Version);Assert.Empty(drafts.Drafts());
        var jobs=new JobGraphStore(ctx.Database);jobs.Apply(ctx.Job,new(id,Guid.NewGuid(),0),"Admin");var original=jobs.Get(ctx.Job)!;
        d=drafts.SaveDraft(id,new(Linear("Revised"),BaseVersion:1),"Admin");Assert.Equal("Linear",new CommercialTemplateStore(ctx.Database).Get(id)!.Definition.Name);
        Assert.Throws<InvalidOperationException>(()=>drafts.SaveDraft(id,new(Linear(),BaseVersion:1),"Admin"));
        drafts.Publish(id,d.Revision,"Admin");Assert.Equal(2,new CommercialTemplateStore(ctx.Database).Get(id)!.Version);
        Assert.Equal(1,jobs.Get(ctx.Job)!.TemplateVersion);Assert.Equal(original.Tasks.Select(t=>t.Id),jobs.Get(ctx.Job)!.Tasks.Select(t=>t.Id));Assert.Equal(2,drafts.Versions(id).Length);
    }
    [StudioPostgresFact] public void InvalidDraftCanBeSavedButCannotBePublished()
    {
        using var ctx=new IntegratedWorkflowTests.WorkflowContext();var drafts=new WorkflowTemplateStore(ctx.Database);Guid id=Guid.NewGuid();
        var definition=Linear() with{Workflow=WorkflowGraphExamples.Linear() with{Edges=[]}};
        var draft=drafts.SaveDraft(id,new(definition),"Admin");Assert.Throws<ArgumentException>(()=>drafts.Publish(id,draft.Revision,"Admin"));Assert.Single(drafts.Drafts());
    }
    [StudioPostgresFact] public void FrontDeskCreatesOneWorkflowWithExplicitServicesAndSharedJoin()
    {
        using var ctx=new IntegratedWorkflowTests.WorkflowContext();var catalog=new PosCatalogStore(ctx.Database);
        var retouch=catalog.Save(new("GRAPH-R","Retouching","Editing",100));var print=catalog.Save(new("GRAPH-P","Printing","Printing",100));
        var definition=JobGraphStore.Normalize(new("Graduation","",[],Workflow:WorkflowGraphExamples.Graduation(retouch.Id,print.Id)));
        var template=new CommercialTemplateStore(ctx.Database).Save(null,"Job",definition,0,true,"Admin");
        var bookings=new FrontDeskBookingRequestStore(ctx.Database);bookings.EnsureSchema();
        var request=new PendingBookingRequest(ctx.Workspace,"Client","client@example.test","Graduation",DateOnly.FromDateTime(DateTime.Today),0,"JMD",null,ExternalBookingId:"graph-booking",JobTemplateId:template.Id,SelectedServiceIds:[retouch.Id]);
        var booking=bookings.Create(request,"Admin");Assert.Equal(booking.JobId,bookings.Create(request,"Admin").JobId);
        var view=new JobGraphStore(ctx.Database).Get(booking.JobId!.Value)!;
        Assert.Single(WorkflowGraphEngine.Flatten(view.Graph).Nodes,n=>n.Id=="join");
        Assert.Equal("Inactive",view.Tasks.Single(t=>t.NodeId=="print").Eligibility);
        Assert.Equal(view.Tasks.Length,Scalar(ctx.Connection,"select count(*)::int from studio_job_activities where job_id=$1",booking.JobId.Value));
        foreach(var node in new[]{"review","capture/shoot","capture/ingest","digital","retouch","quality","delivery"})
        {
            var task=view.Tasks.Single(t=>t.NodeId==node);CommercialTemplateStore.GuardTask(ctx.Connection,null,task.Id,"Completed");
            Sql(ctx.Connection,"update studio_job_activities set status='Completed',completed_at_utc=now() where id=$1",task.Id);
        }
        Assert.True(new JobGraphStore(ctx.Database).Get(booking.JobId.Value)!.Evaluation.Completed);
    }
    [StudioPostgresFact] public void TemplateChangesRequireConfirmationPreserveCompletedWorkAndReplayWithoutDuplicates()
    {
        using var ctx=new IntegratedWorkflowTests.WorkflowContext();var templates=new CommercialTemplateStore(ctx.Database);var a=templates.Save(null,"Job",Linear(),0,true,"Admin");
        var b=templates.Save(null,"Job",JobGraphStore.Normalize(new("Other","",[new("new","New task")],Workflow:WorkflowGraphEngine.FromSteps([new("new","New task")]))),0,true,"Admin");
        var store=new JobGraphStore(ctx.Database);store.Apply(ctx.Job,new(a.Id,Guid.NewGuid(),0),"Admin");var view=store.Get(ctx.Job)!;
        Guid completed=view.Tasks.Single(t=>t.NodeId=="review").Id;Sql(ctx.Connection,"update studio_job_activities set status='Completed',completed_at_utc=now() where id=$1",completed);
        var impact=store.Impact(ctx.Job,b.Id);Assert.True(impact.RequiresConfirmation);Assert.Contains("review",impact.CompletedAffected);
        var request=new ApplyJobTemplateRequest(b.Id,Guid.NewGuid(),store.Get(ctx.Job)!.Revision);
        Assert.Throws<ArgumentException>(()=>store.Apply(ctx.Job,request,"Admin"));
        request=request with{ConfirmChanges=true,Reason="Client requested printing only"};store.Apply(ctx.Job,request,"Admin");store.Apply(ctx.Job,request,"Admin");
        Assert.Equal("Completed",Scalar(ctx.Connection,"select status from studio_job_activities where id=$1",completed));
        Assert.Single(store.Get(ctx.Job)!.Tasks);Assert.Equal(3,store.Get(ctx.Job)!.History.Length);
        Assert.Throws<ArgumentException>(()=>store.Apply(ctx.Job,request with{Reason="Different"},"Admin"));
        Assert.Equal(1,Scalar(ctx.Connection,"select count(*)::int from studio_job_workflow_history where job_id=$1",ctx.Job));
    }
    [StudioPostgresFact] public void ServiceRemovalProtectsStartedWorkAndNeverEditsPostedCharges()
    {
        using var ctx=new IntegratedWorkflowTests.WorkflowContext();var catalog=new PosCatalogStore(ctx.Database);var retouch=catalog.Save(new("GRAPH-R2","Retouch","Editing",100));var print=catalog.Save(new("GRAPH-P2","Print","Printing",100));
        var template=new CommercialTemplateStore(ctx.Database).Save(null,"Job",JobGraphStore.Normalize(new("Graduation","",[],Workflow:WorkflowGraphExamples.Graduation(retouch.Id,print.Id))),0,true,"Admin");
        var store=new JobGraphStore(ctx.Database);store.Apply(ctx.Job,new(template.Id,Guid.NewGuid(),0),"Admin");
        store.SetServices(ctx.Job,new([retouch.Id],Guid.NewGuid(),store.Get(ctx.Job)!.Revision),"Admin");var view=store.Get(ctx.Job)!;
        var pos=new StudioPosStore(ctx.Database,new PosAuditStore(ctx.Database));var studio=new StudioStore(ctx.Database,new WorkspaceStore(ctx.Database));
        var sale=pos.Create(ctx.Job,new([new(retouch.Code,retouch.Name,100,1,CatalogItemId:retouch.Id)],Guid.NewGuid()),studio,"Admin");
        pos.Complete(sale.Id,new([new(TenderKind.Cash,100,null)],"Test"),studio,"Admin");
        var before=pos.Get(sale.Id)!;
        Sql(ctx.Connection,"update studio_job_activities set status='Completed' where job_id=$1 and workflow_node_id in ('review','capture/shoot','capture/ingest')",ctx.Job);
        var task=view.Tasks.Single(t=>t.NodeId=="retouch");CommercialTemplateStore.GuardTask(ctx.Connection,null,task.Id,"InProgress");Sql(ctx.Connection,"update studio_job_activities set status='InProgress',started_at_utc=now() where id=$1",task.Id);
        var remove=new WorkflowServicesRequest([],Guid.NewGuid(),store.Get(ctx.Job)!.Revision);
        Assert.Throws<ArgumentException>(()=>store.SetServices(ctx.Job,remove,"Admin"));store.SetServices(ctx.Job,remove with{ConfirmChanges=true,Reason="Preserve started work; adjust billing separately"},"Admin");
        CommercialTemplateStore.GuardTask(ctx.Connection,null,task.Id,"Completed");Assert.Equal("InProgress",store.Get(ctx.Job)!.Tasks.Single(t=>t.Id==task.Id).Eligibility);
        Assert.Equal(1,Scalar(ctx.Connection,"select count(*)::int from studio_pos_sales"));Assert.Equal(0,Scalar(ctx.Connection,"select count(*)::int from studio_pos_refunds"));
        var after=pos.Get(sale.Id)!;Assert.Equal(before.Total,after.Total);Assert.Equal(before.Status,after.Status);Assert.Equal(before.Lines.Single().Id,after.Lines.Single().Id);Assert.Equal(before.Tenders.Count,after.Tenders.Count);
        Assert.Throws<ArgumentException>(()=>CommercialTemplateStore.GuardTask(ctx.Connection,null,view.Tasks.Single(t=>t.NodeId=="print").Id,"Completed"));
    }
    [StudioPostgresFact] public async Task ConcurrentTemplateUpdatesUseOneRevisionAndCannotCreateDuplicateTasks()
    {
        using var ctx=new IntegratedWorkflowTests.WorkflowContext();var template=new CommercialTemplateStore(ctx.Database).Save(null,"Job",Linear(),0,true,"Admin");
        var store=new JobGraphStore(ctx.Database);var request=new ApplyJobTemplateRequest(template.Id,Guid.NewGuid(),0);
        async Task<bool> Apply()=>await Task.Run(()=>{try{store.Apply(ctx.Job,request with{OperationId=Guid.NewGuid()},"Admin");return true;}catch(InvalidOperationException){return false;}});
        var outcomes=await Task.WhenAll(Apply(),Apply());Assert.Single(outcomes,x=>x);Assert.Equal(3,store.Get(ctx.Job)!.Tasks.Length);
    }
    [StudioPostgresFact] public void ProductionSubmissionPrecedesDeliveryAndTheDomainEventCompletesItsGraphMilestone()
    {
        using var ctx=new IntegratedWorkflowTests.WorkflowContext();var template=new CommercialTemplateStore(ctx.Database).Save(null,"Job",Linear(),0,true,"Admin");
        var store=new JobGraphStore(ctx.Database);store.Apply(ctx.Job,new(template.Id,Guid.NewGuid(),0),"Admin");
        Assert.Throws<ArgumentException>(()=>JobGraphStore.GuardProductionReady(ctx.Connection,null,ctx.Job));
        Sql(ctx.Connection,"update studio_job_activities set status='Completed',completed_at_utc=now() where job_id=$1 and workflow_node_id in ('review','produce')",ctx.Job);
        JobGraphStore.GuardProductionReady(ctx.Connection,null,ctx.Job);
        Assert.Throws<ArgumentException>(()=>JobGraphStore.GuardCompletion(ctx.Connection,null,ctx.Job));
        using(var tx=ctx.Connection.BeginTransaction()){JobGraphStore.CompleteMilestone(ctx.Connection,tx,ctx.Job,"Delivery");tx.Commit();}
        Assert.True(store.Get(ctx.Job)!.Evaluation.Completed);JobGraphStore.GuardCompletion(ctx.Connection,null,ctx.Job);
        var completed=Scalar(ctx.Connection,"select completed_at_utc from studio_job_activities where job_id=$1 and workflow_node_id='delivery'",ctx.Job);
        using(var tx=ctx.Connection.BeginTransaction()){JobGraphStore.CompleteMilestone(ctx.Connection,tx,ctx.Job,"Delivery");tx.Commit();}
        Assert.Equal(completed,Scalar(ctx.Connection,"select completed_at_utc from studio_job_activities where job_id=$1 and workflow_node_id='delivery'",ctx.Job));
    }
}
