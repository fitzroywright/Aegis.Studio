using System.Security.Claims;
using System.Text.Json;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Npgsql;
using static Aegis.Studio.Web.Messaging.StudioUnifiedMessaging;

namespace Aegis.Studio.Web.Email;

public sealed record InquiryClient(Guid Id,string Name,string? Email);
public sealed record InquiryAssociation(Guid? ClientId,string? ClientName,string Reference,InquiryClient[] Candidates,bool CanEdit,object[] History,bool CanCreateJob);
public sealed record ChangeInquiryClient(Guid? ClientId);
public sealed record ChangeInquiryJob(Guid? JobId);
public sealed record AddInquiryContact(string Address);
public sealed record MoveInquiryMessage(Guid? ConversationId,Guid? JobId,bool NewInquiry=false);
public sealed record ConfirmEmailRouting(Guid? ConversationId,Guid? JobId,Guid? WorkspaceId,bool NewInquiry=false,bool AddContact=false);

public sealed class StudioInquiryAssociations(StudioDatabase database,StudioStore studio,
    WorkspaceAccessService workspaces,JobAccessService jobs)
{
    public void EnsureSchema()
    {
        using var stream=typeof(StudioInquiryAssociations).Assembly.GetManifestResourceStream("Aegis.Studio.Web.Email.EmailAssociations.sql")!;
        using var reader=new StreamReader(stream);using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        using var q=new NpgsqlCommand(reader.ReadToEnd(),c,tx);q.ExecuteNonQuery();tx.Commit();
    }
    public void AssertAccess(Guid id,ClaimsPrincipal user,bool edit)
    {
        var conversation=studio.GetConversation(id)??throw new KeyNotFoundException();
        // A job conversation requires job permission even if the user can view the workspace.
        bool allowed=conversation.JobId is Guid job
            ? jobs.HasPermission(user,job,edit?"EditJob":"ViewConversation") && (!edit||jobs.HasPermission(user,job,"ReplyRequester"))
            : workspaces.HasPermission(user,conversation.WorkspaceId,edit?"ReplyRequester":"View");
        if(!allowed)throw new UnauthorizedAccessException();
    }
    public bool CanAccess(Guid id,ClaimsPrincipal user,bool edit=false)
    {try{AssertAccess(id,user,edit);return true;}catch(UnauthorizedAccessException){return false;}catch(KeyNotFoundException){return false;}}
    public void AssertWorkspace(Guid workspace,ClaimsPrincipal user)
    {if(!workspaces.HasPermission(user,workspace,"ReplyRequester"))throw new UnauthorizedAccessException();}
    public bool CanReviewThread(Guid thread,ClaimsPrincipal user)
    {
        using var c=database.OpenConnection();using var q=new NpgsqlCommand("select scope_type,scope_id,exists(select 1 from studio_message_thread_members where thread_id=$1 and lower(user_name)=lower($2)) from studio_message_threads where id=$1",c);
        Add(q,thread,CurrentUser(user));using var r=q.ExecuteReader();if(!r.Read())return false;
        string scope=r.GetString(0);Guid? id=r.IsDBNull(1)?null:r.GetGuid(1);bool member=r.GetBoolean(2);r.Close();
        return scope=="Workspace"&&id.HasValue?workspaces.HasPermission(user,id.Value,"ReplyRequester"):
            scope=="Job"&&id.HasValue?jobs.HasPermission(user,id.Value,"EditJob")&&jobs.HasPermission(user,id.Value,"ReplyRequester"):scope=="Direct"&&member;
    }
    public void MatchClient(Guid id,string sender)
    {
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        using var q=new NpgsqlCommand("select client_id,client_association_locked from studio_conversations where id=$1 for update",c,tx);Add(q,id);
        using var r=q.ExecuteReader();if(!r.Read())return;bool assigned=!r.IsDBNull(0),locked=r.GetBoolean(1);r.Close();
        if(assigned||locked)return;
        var candidates=Candidates(c,tx,sender);
        if(candidates.Length==1)
        {
            using var update=new NpgsqlCommand("update studio_conversations set client_id=$2 where id=$1 and job_id is null",c,tx);Add(update,id,candidates[0].Id);
            if(update.ExecuteNonQuery()==1)History(c,tx,id,null,"Automatic client match","Studio email intake",null,candidates[0].Id);
        }
        tx.Commit();
    }
    public InquiryAssociation Get(Guid id,ClaimsPrincipal user)
    {
        AssertAccess(id,user,false);var conversation=studio.GetConversation(id)!;
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        using var q=new NpgsqlCommand("select case when j.id is null then v.client_id else j.client_id end,cl.display_name from studio_conversations v left join studio_jobs j on j.id=v.job_id left join studio_clients cl on cl.id=case when j.id is null then v.client_id else j.client_id end where v.id=$1",c,tx);
        Add(q,id);using var r=q.ExecuteReader();r.Read();Guid? client=r.IsDBNull(0)?null:r.GetGuid(0);string? name=r.IsDBNull(1)?null:r.GetString(1);r.Close();
        var candidates=Candidates(c,tx,conversation.ContactAddress);var history=ReadHistory(c,tx,id);
        tx.Commit();return new(client,name,StudioEmailRouting.PublicReference(id),candidates,CanAccess(id,user,true),history,conversation.JobId is null && workspaces.HasPermission(user,conversation.WorkspaceId,"CreateJob"));
    }
    public InquiryClient[] Search(Guid id,ClaimsPrincipal user,string? query)
    {
        AssertAccess(id,user,true);using var c=database.OpenConnection();using var q=new NpgsqlCommand("select id,display_name,email from studio_clients where $1='' or strpos(lower(display_name||' '||coalesce(email,'')),$1)>0 order by display_name limit 50",c);
        Add(q,query?.Trim().ToLowerInvariant()??"");using var r=q.ExecuteReader();List<InquiryClient> rows=[];while(r.Read())rows.Add(new(r.GetGuid(0),r.GetString(1),r.IsDBNull(2)?null:r.GetString(2)));return rows.ToArray();
    }
    public void SetClient(Guid id,Guid? client,ClaimsPrincipal user)
    {
        AssertAccess(id,user,true);using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        using var prior=new NpgsqlCommand("select client_id,job_id from studio_conversations where id=$1 for update",c,tx);Add(prior,id);
        using var r=prior.ExecuteReader();r.Read();Guid? old=r.IsDBNull(0)?null:r.GetGuid(0),job=r.IsDBNull(1)?null:r.GetGuid(1);r.Close();
        // Re-check under the promotion lock so permissions cannot race job creation.
        if(job is Guid j && (!jobs.HasPermission(user,j,"EditJob")||!jobs.HasPermission(user,j,"ReplyRequester")))throw new UnauthorizedAccessException();
        if(client.HasValue){using var exists=new NpgsqlCommand("select exists(select 1 from studio_clients where id=$1)",c,tx);Add(exists,client);if(!(bool)exists.ExecuteScalar()!)throw new KeyNotFoundException();}
        using var update=new NpgsqlCommand("update studio_conversations set client_id=$2,client_association_locked=true where id=$1",c,tx);Add(update,id,client);update.ExecuteNonQuery();
        if(job.HasValue){using var updateJob=new NpgsqlCommand("update studio_jobs set client_id=$2,client_name=coalesce((select display_name from studio_clients where id=$2),client_name) where id=$1",c,tx);Add(updateJob,job,client);updateJob.ExecuteNonQuery();}
        History(c,tx,id,null,"Change client association",CurrentUser(user),old,client);tx.Commit();
    }
    public void SetJob(Guid id,Guid? job,ClaimsPrincipal user)
    {
        AssertAccess(id,user,true);if(job.HasValue)AssertJob(job.Value,user);
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        using var prior=new NpgsqlCommand("select job_id from studio_conversations where id=$1 for update",c,tx);Add(prior,id);object? old=prior.ExecuteScalar();
        if(old is Guid oldJob)
        {
            AssertJob(oldJob,user);
            if(studio.GetJob(oldJob)?.ConversationId==id && job!=oldJob)
                throw new InvalidOperationException("This is the job’s original inquiry. Move the received email to separate work instead, preserving the original job history.");
        }
        using var update=new NpgsqlCommand("update studio_conversations set job_id=$2 where id=$1",c,tx);Add(update,id,job);update.ExecuteNonQuery();
        History(c,tx,id,null,"Change job association",CurrentUser(user),old is Guid g?g:null,job);tx.Commit();
    }
    public Guid ConversationForJob(Guid job,ClaimsPrincipal user)
    {AssertJob(job,user);return studio.GetJob(job)?.ConversationId??throw new KeyNotFoundException();}
    private void AssertJob(Guid job,ClaimsPrincipal user)
    {if(!jobs.HasPermission(user,job,"EditJob")||!jobs.HasPermission(user,job,"ReplyRequester"))throw new UnauthorizedAccessException();}
    public void AddContact(Guid id,string address,ClaimsPrincipal user)
    {
        AssertAccess(id,user,true);string normalized=StudioEmailRouting.NormalizeAddress(address);if(normalized=="")throw new InvalidOperationException("Enter a valid contact email.");
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        using var q=new NpgsqlCommand("insert into studio_email_contacts(conversation_id,address,added_by) values($1,$2,$3) on conflict do nothing",c,tx);Add(q,id,normalized,CurrentUser(user));
        if(q.ExecuteNonQuery()>0)History(c,tx,id,null,"Add alternate contact",CurrentUser(user),null,normalized);tx.Commit();
    }
    public Guid MoveMessage(Guid source,Guid message,MoveInquiryMessage request,ClaimsPrincipal user)
    {
        AssertAccess(source,user,true);Guid? target=request.JobId.HasValue?ConversationForJob(request.JobId.Value,user):request.ConversationId;
        if(target.HasValue)AssertAccess(target.Value,user,true);
        else if(!request.NewInquiry)throw new InvalidOperationException("Choose an inquiry, a job, or new work.");
        var original=studio.GetConversation(source)!;
        if(!target.HasValue && !workspaces.HasPermission(user,original.WorkspaceId,"ReplyRequester"))throw new UnauthorizedAccessException();
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        using var find=new NpgsqlCommand("select external_message_id,coalesce(author_user_name,''),author_display_name,body,created_at_utc from studio_messages where id=$1 and conversation_id=$2 for update",c,tx);Add(find,message,source);
        using var r=find.ExecuteReader();if(!r.Read())throw new KeyNotFoundException();string? external=r.IsDBNull(0)?null:r.GetString(0);string sender=r.GetString(1),name=r.GetString(2);r.Close();
        if(external is not null)LockRequest(c,tx,"email-intake:"+external);
        if(external is null)throw new InvalidOperationException("Only received email can be split or moved; sent messages retain their delivery history.");
        if(!target.HasValue)
        {
            target=Guid.NewGuid();using var create=new NpgsqlCommand("insert into studio_conversations(id,workspace_id,subject,contact_name,contact_address,channel,is_closed,created_at_utc,client_id,client_association_locked) select $2,workspace_id,subject,$3,$4,'Email',false,now(),client_id,true from studio_conversations where id=$1",c,tx);Add(create,source,target,name,sender);create.ExecuteNonQuery();
        }
        using(var move=new NpgsqlCommand("update studio_messages set conversation_id=$2 where id=$1",c,tx)){Add(move,message,target);move.ExecuteNonQuery();}
        using(var files=new NpgsqlCommand("update studio_attachments set conversation_id=$2 where id in(select attachment_id from studio_email_attachment_links where message_id=$1)",c,tx)){Add(files,message,target);files.ExecuteNonQuery();}
        // Older mail lacks per-message attachment links. Refuse an unsafe partial move.
        using(var legacy=new NpgsqlCommand("select exists(select 1 from studio_attachments a where a.conversation_id=$1 and not exists(select 1 from studio_email_attachment_links l where l.attachment_id=a.id))",c,tx))
        {Add(legacy,source);if((bool)legacy.ExecuteScalar()!)throw new InvalidOperationException("This historical conversation has attachments without message ownership. Reconcile attachment ownership before splitting it.");}
        using(var receipt=new NpgsqlCommand("update studio_email_intake_receipts set conversation_id=$2,thread_id=null,staff_corrected=true where external_message_id=$1",c,tx)){Add(receipt,external,target);receipt.ExecuteNonQuery();}
        History(c,tx,target,external,"Move received email",CurrentUser(user),source,target);History(c,tx,source,external,"Move received email",CurrentUser(user),source,target);tx.Commit();return target.Value;
    }
    internal static void History(NpgsqlConnection c,NpgsqlTransaction tx,Guid? conversation,string? external,string action,string actor,object? previous,object? next)
    {
        using var q=new NpgsqlCommand("insert into studio_email_association_history(conversation_id,external_message_id,action,actor,previous_value,new_value) values($1,$2,$3,$4,$5::jsonb,$6::jsonb)",c,tx);
        Add(q,conversation,external,action,actor,JsonSerializer.Serialize(previous),JsonSerializer.Serialize(next));q.ExecuteNonQuery();
    }
    private static InquiryClient[] Candidates(NpgsqlConnection c,NpgsqlTransaction tx,string address)
    {
        using var q=new NpgsqlCommand("select id,display_name,email from studio_clients where studio_normalize_email(email)=$1 and $1<>'' order by display_name",c,tx);Add(q,StudioEmailRouting.NormalizeAddress(address));using var r=q.ExecuteReader();List<InquiryClient> rows=[];
        while(r.Read())rows.Add(new(r.GetGuid(0),r.GetString(1),r.IsDBNull(2)?null:r.GetString(2)));return rows.ToArray();
    }
    private static object[] ReadHistory(NpgsqlConnection c,NpgsqlTransaction tx,Guid id)
    {
        using var q=new NpgsqlCommand("select action,actor,previous_value::text,new_value::text,created_at_utc from studio_email_association_history where conversation_id=$1 order by id desc limit 100",c,tx);Add(q,id);using var r=q.ExecuteReader();List<object> rows=[];
        while(r.Read())rows.Add(new{action=r.GetString(0),actor=r.GetString(1),previous=r.GetString(2),next=r.GetString(3),at=r.GetFieldValue<DateTimeOffset>(4)});return rows.ToArray();
    }
}
