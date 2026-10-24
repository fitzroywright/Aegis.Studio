using System.Security.Claims;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Scheduling;
using Aegis.Studio.Web.Workflow;
using Aegis.Studio.Web.Versioning;

namespace Aegis.Studio.Web.WorkDay;

public sealed record WorkDayMediaSubject(Guid Id,string Name,string Collection);
public sealed record WorkDayMediaDestination(Guid JobId,string Title,Guid? SessionId,IReadOnlyList<string> Collections,IReadOnlyList<WorkDayMediaSubject> Subjects,DateTimeOffset? StartsAtUtc=null,DateTimeOffset? EndsAtUtc=null,string? Location=null,string? Reference=null);

public static class WorkDayMediaDestinations
{
    public static Dictionary<string,WorkDayMediaDestination> Build(WorkDayBootstrap bootstrap,ClaimsPrincipal user,
        StudioDatabase database,ScheduleStore schedule,JobAccessService access)
    {
        var result=new Dictionary<string,WorkDayMediaDestination>();
        var ids=bootstrap.Today.Concat(bootstrap.Upcoming).Select(x=>x.Id).ToHashSet();
        if(new PhotographerStore(database).Get(bootstrap.UserName) is { IsActive:true } profile)
            foreach(var entry in schedule.GetEntries(DateTimeOffset.UtcNow.AddDays(-60),DateTimeOffset.UtcNow.AddDays(30)).Where(x=>x.ResourceIds.Contains(profile.ScheduleResourceId)))ids.Add(entry.Id);
        foreach(var id in ids)
        {
            var destination=Resolve(id,bootstrap.UserName,user,database,schedule,access);
            if(destination is not null)result[id.ToString("D")]=destination;
        }
        return result;
    }

    public static WorkDayMediaDestination? Resolve(Guid assignment,string userName,ClaimsPrincipal user,
        StudioDatabase database,ScheduleStore schedule,JobAccessService access)
    {
        if(!WorkDayEndpoints.AssignmentAllowed(user,userName,assignment,database,schedule,access,"AddAttachments"))return null;
        var entry=schedule.GetEntries(entryId:assignment).SingleOrDefault();
        if(entry is null||entry.Status=="Cancelled")return null;
        Guid? job=WorkDayEndpoints.ResolveAssignmentJobId(entry,database);
        if(job is not Guid id)return null;
        using var c=database.OpenConnection();
        string title,status;
        using(var q=WorkflowStore.Command(c,null,"select title,status from studio_jobs where id=$1",id))
        using(var r=q.ExecuteReader()){if(!r.Read())return null;title=r.GetString(0);status=r.GetString(1);}
        if(!StudioVersionStore.AllowsMediaChange(status)||status=="Archived")return null;
        Guid? session=WorkflowStore.Scalar(c,null,"select s.id from studio_workflow_sessions s join studio_shoot_sessions shoot on shoot.id=s.shoot_id where s.job_id=$1 and shoot.schedule_entry_id=$2 and shoot.status<>'Cancelled'",id,assignment) as Guid?;
        var collections=new List<string>{"Unclassified"};
        using(var q=WorkflowStore.Command(c,null,"select name from studio_media_collections where job_id=$1 order by name",id))
        using(var r=q.ExecuteReader())while(r.Read())if(!collections.Contains(r.GetString(0)))collections.Add(r.GetString(0));
        var subjects=new List<WorkDayMediaSubject>();
        using(var q=WorkflowStore.Command(c,null,"select id,name,collection_name from studio_job_subjects where job_id=$1 and not is_system order by name",id))
        using(var r=q.ExecuteReader())while(r.Read())subjects.Add(new(r.GetGuid(0),r.GetString(1),r.GetString(2)));
        string? reference=WorkflowStore.Scalar(c,null,"select c.intake_reference from studio_jobs j left join studio_conversations c on c.id=j.conversation_id where j.id=$1",id) as string;
        return new(id,title,session,collections,subjects.Where(s=>collections.Contains(s.Collection)).ToArray(),entry.StartsAtUtc,entry.EndsAtUtc,entry.Location,reference);
    }
}
