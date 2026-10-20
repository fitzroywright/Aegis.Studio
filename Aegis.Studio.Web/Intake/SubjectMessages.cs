using System.Security.Claims;
using System.Text.Json;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Workflow;
using Npgsql;
namespace Aegis.Studio.Web.Intake;
public sealed record SubjectRecipient(Guid SubjectId,string Email);
public sealed record SubjectSend(Guid OperationId,SubjectRecipient[] Recipients,string Subject,string Body);
public static class SubjectMessages
{
    public static void Map(WebApplication app)
    {
        using(var c=app.Services.GetRequiredService<StudioDatabase>().OpenConnection())WorkflowStore.Sql(c,null,"create table if not exists studio_subject_sends(operation_id uuid primary key,job_id uuid not null,actor text not null,fingerprint text not null,deliveries uuid[] not null)");
        app.MapPost("/api/jobs/{job:guid}/subjects/messages",(Guid job,SubjectSend input,ClaimsPrincipal user,StudioDatabase db,JobAccessService access)=>
        {
            if(!access.HasPermission(user,job,"ReplyRequester"))return Results.Forbid();
            try{return Results.Ok(Send(db,job,input,user.Identity?.Name??"Staff"));}catch(ArgumentException e){return Results.BadRequest(new{error=e.Message});}
        }).RequireAuthorization();
    }
    static object Send(StudioDatabase db,Guid job,SubjectSend input,string actor)
    {
        if(input.OperationId==Guid.Empty||input.Recipients is null||input.Recipients.Length is <1 or >500||input.Recipients.Select(x=>x.SubjectId).Distinct().Count()!=input.Recipients.Length||string.IsNullOrWhiteSpace(input.Subject)||input.Subject.Length>200||string.IsNullOrWhiteSpace(input.Body)||input.Body.Length>4000)throw new ArgumentException("Choose subjects and supply a subject and message.");
        using var c=db.OpenConnection();using var tx=c.BeginTransaction();WorkflowStore.Sql(c,tx,"select pg_advisory_xact_lock(hashtextextended($1,0))","subject-send:"+input.OperationId);
        var fingerprint=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input)));
        using(var q=WorkflowStore.Command(c,tx,"select fingerprint,deliveries from studio_subject_sends where operation_id=$1 and job_id=$2 and actor=$3",input.OperationId,job,actor))using(var r=q.ExecuteReader())if(r.Read()){if(r.GetString(0)!=fingerprint)throw new ArgumentException("Operation ID reused with different content.");return new{deliveryIds=r.GetFieldValue<Guid[]>(1),state="AlreadyQueued"};}
        var ids=new List<Guid>();
        foreach(var recipient in input.Recipients)
        {
            Guid cv;string email,from;
            using(var q=WorkflowStore.Command(c,tx,"select j.conversation_id,s.contact_email,w.email_address from studio_job_subjects s join studio_jobs j on j.id=s.job_id join studio_workspaces w on w.id=j.workspace_id where s.id=$1 and j.id=$2 and not s.is_system",recipient.SubjectId,job))using(var r=q.ExecuteReader())
            {if(!r.Read()||r.IsDBNull(2))throw new ArgumentException("Subject or sending identity unavailable.");cv=r.GetGuid(0);email=r.GetString(1);from=r.GetString(2);}
            if(!System.Net.Mail.MailAddress.TryCreate(email,out _)||!email.Equals(recipient.Email,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Subject contact changed or is invalid. Review recipients again.");
            Guid id=Guid.NewGuid();ids.Add(id);
            WorkflowStore.Sql(c,tx,"insert into studio_outbound_email(id,conversation_id,from_address,to_address,subject,body,status,kind,sent_by_user_name,sending_identity_type,created_at_utc,delivery_metadata) values($1,$2,$3,$4,$5,$6,'Pending','SubjectMessage',$7,'Workspace',now(),$8::jsonb)",id,cv,from,email,input.Subject,input.Body,actor,JsonSerializer.Serialize(new{SubjectId=recipient.SubjectId,JobId=job,OperationId=input.OperationId}));
        }
        WorkflowStore.Sql(c,tx,"insert into studio_subject_sends values($1,$2,$3,$4,$5)",input.OperationId,job,actor,fingerprint,ids.ToArray());tx.Commit();return new{deliveryIds=ids,state="Queued"};
    }
}
