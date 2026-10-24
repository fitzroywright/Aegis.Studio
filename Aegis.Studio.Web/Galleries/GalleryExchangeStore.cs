using System.Security.Claims;
using System.Text.Json;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Workflow;
using Npgsql;

namespace Aegis.Studio.Web.Galleries;

public sealed record GalleryRequestReview(string State,Guid? JobId,Guid? ClientId,string VerificationReference,string Reason,int ExpectedVersion);
public sealed record GalleryTransferCommand(Guid PublicationId,int ExpectedVersion,bool RepublishApproval=false);
public sealed record GalleryRevocationCommand(string Reason);

public sealed class GalleryExchangeStore(StudioDatabase database,WorkflowStore workflow,JobAccessService access,IConfiguration configuration)
{
    public static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public static void Ensure(StudioDatabase db)
    {
        using var c=db.OpenConnection();
        WorkflowStore.Sql(c,null,"""
          create table if not exists studio_gallery_requests(id uuid primary key,integration_id text not null,external_reference text not null,requester_reference text not null,
            payload jsonb not null,state text not null default 'Pending',job_id uuid references studio_jobs(id),client_id uuid references studio_clients(id),
            verification_reference text,reason text,decision_version integer not null default 0,remote_state jsonb not null default '{}',created_at_utc timestamptz not null default now());
          alter table studio_gallery_requests add column if not exists last_polled_at_utc timestamptz;
          alter table studio_gallery_requests add column if not exists last_remote_sequence bigint not null default 0;
          alter table studio_gallery_requests add column if not exists poll_error text;
          create table if not exists studio_gallery_transfers(id uuid primary key,request_id uuid not null references studio_gallery_requests(id),
            version integer not null,manifest jsonb not null,state text not null default 'Pending synchronization',created_by text not null,created_at_utc timestamptz not null default now(),unique(request_id,version));
          create table if not exists studio_gallery_revocations(id uuid primary key,request_id uuid not null references studio_gallery_requests(id),
            payload jsonb not null,state text not null default 'Pending synchronization',created_at_utc timestamptz not null default now());
          create table if not exists studio_gallery_exchange_outbox(sequence bigserial primary key,id uuid not null unique,request_id uuid not null references studio_gallery_requests(id),
            kind text not null,payload jsonb not null,status text not null default 'Pending',attempts integer not null default 0,last_error text,next_attempt_at_utc timestamptz not null default now(),created_at_utc timestamptz not null default now());
          alter table studio_gallery_exchange_outbox add column if not exists last_attempt_at_utc timestamptz;
          create table if not exists studio_gallery_exchange_cursor(source text primary key,value bigint not null);
          create table if not exists studio_exchange_activity(sequence bigserial primary key,id uuid not null unique,aggregate_id uuid not null,
            job_id uuid,action text not null,actor text not null,details jsonb not null,occurred_at_utc timestamptz not null default now(),recorded_at_utc timestamptz not null default now(),remote_id uuid unique);
          create index if not exists ix_studio_exchange_activity_aggregate on studio_exchange_activity(aggregate_id,sequence);
          """+Aegis.Studio.Web.WorkDay.RemoteMediaImportWorker.Schema);
    }
    public static void Record(NpgsqlConnection c,NpgsqlTransaction tx,Guid aggregate,Guid? job,string action,string actor,string details)
        =>WorkflowStore.Sql(c,tx,"insert into studio_exchange_activity(id,aggregate_id,job_id,action,actor,details) values($1,$2,$3,$4,$5,$6::jsonb)",
            Guid.NewGuid(),aggregate,job,action,actor,details);
    public object List(ClaimsPrincipal user,Guid? job=null)
    {
        using var c=database.OpenConnection();
        var allowed=access.GetAccessibleJobIds(user,true).ToArray();bool desk=workflow.IsFrontDesk(user);
        return ReadJson(c,"""
          select coalesce(jsonb_agg(jsonb_build_object('id',r.id,'integrationId',r.integration_id,'externalReference',r.external_reference,
            'hints',r.payload->'hints','state',r.state,'jobId',r.job_id,'clientId',r.client_id,'verificationReference',r.verification_reference,
            'reason',r.reason,'pollError',r.poll_error,'decisionVersion',r.decision_version,'remote',r.remote_state,'createdAt',r.created_at_utc,
            'transfers',coalesce((select jsonb_agg(jsonb_build_object('id',t.id,'version',t.version,'state',t.state,'createdAt',t.created_at_utc) order by t.version desc) from studio_gallery_transfers t where t.request_id=r.id),'[]'::jsonb),
            'revocations',coalesce((select jsonb_agg(jsonb_build_object('id',v.id,'state',v.state,'throughVersion',v.payload->'throughVersion','createdAt',v.created_at_utc,'overdue',v.state<>'Completed' and v.created_at_utc<now()-make_interval(mins=>$4::integer)) order by v.created_at_utc desc) from studio_gallery_revocations v where v.request_id=r.id),'[]'::jsonb),
            'queue',coalesce((select jsonb_agg(jsonb_build_object('kind',o.kind,'status',o.status,'attempts',o.attempts,'error',o.last_error,'nextAttemptAt',o.next_attempt_at_utc,'lastAttemptAt',o.last_attempt_at_utc)) from studio_gallery_exchange_outbox o where o.request_id=r.id and o.status='Pending'),'[]'::jsonb)
          ) order by r.created_at_utc desc),'[]'::jsonb)::text from studio_gallery_requests r
          where ($1 or r.job_id=any($2)) and ($3::uuid is null or r.job_id=$3 or ($1 and r.job_id is null))
          """,desk,allowed,job,Math.Clamp(configuration.GetValue<int?>("PublicSite:GalleryRevocationOverdueMinutes")??60,5,10080));
    }
    public object Jobs(ClaimsPrincipal user)
    {
        using var c=database.OpenConnection();
        var publish=access.GetPermissionMap(user).Where(x=>x.Value.ManageProduction||workflow.CanManage(user,x.Key)).Select(x=>x.Key).ToArray();
        return ReadJson(c,"""
          select coalesce(jsonb_agg(jsonb_build_object('id',j.id,'title',j.title,'clientId',j.client_id,'clientName',cl.display_name,'canPublish',j.id=any($3),'canReview',($1 or j.id=any($3)),
            'publications',coalesce((select jsonb_agg(jsonb_build_object('id',p.id,'reference',p.reference,'createdAt',p.created_at_utc))
              from studio_workflow_publications p join studio_workflow_submissions s on s.id=p.submission_id where p.job_id=j.id and s.status='Approved' and s.kind='Finished'),'[]'::jsonb)) order by j.title),'[]'::jsonb)::text
          from studio_jobs j left join studio_clients cl on cl.id=j.client_id where $1 or j.id=any($2)
          """,workflow.IsFrontDesk(user),access.GetAccessibleJobIds(user,true).ToArray(),publish);
    }
    public object Activity(Guid id,ClaimsPrincipal user)
    {
        AssertRead(id,user);using var c=database.OpenConnection();return ReadJson(c,"""
          select coalesce(jsonb_agg(jsonb_build_object('id',id,'action',action,'actor',actor,'details',details,'occurredAt',occurred_at_utc,'recordedAt',recorded_at_utc) order by sequence desc),'[]'::jsonb)::text
          from (select * from studio_exchange_activity where aggregate_id=$1 order by sequence desc limit 500) a
          """,id);
    }
    public object Review(Guid id,GalleryRequestReview request,ClaimsPrincipal user)
    {
        AssertWrite(id,user,request.JobId);string actor=WorkflowStore.Name(user);
        if(request.State is not ("Approved" or "Rejected" or "Held")||request.Reason is null||request.VerificationReference is null||request.Reason.Length>1000||request.VerificationReference.Length>500)
            throw new ArgumentException("Choose Approved, Held or Rejected with bounded reason and verification reference.");
        if(request.State=="Approved"&&(request.JobId is null||request.ClientId is null||string.IsNullOrWhiteSpace(request.VerificationReference)))
            throw new ArgumentException("Approval requires a job, its client, and a trusted-contact verification reference.");
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        var old=Locked(c,tx,id);if(old.DecisionVersion!=request.ExpectedVersion)throw new ArgumentException("Another staff decision was saved. Refresh the request.");
        if(request.State=="Approved"&&!WorkflowStore.Exists(c,tx,"select exists(select 1 from studio_jobs where id=$1 and client_id=$2)",request.JobId,request.ClientId))
            throw new ArgumentException("The selected client must own the selected job.");
        bool hasTransfers=WorkflowStore.Exists(c,tx,"select exists(select 1 from studio_gallery_transfers where request_id=$1)",id);
        if(hasTransfers&&(old.JobId!=request.JobId||old.ClientId!=request.ClientId))throw new ArgumentException("A released request cannot be moved to another client or job. Withdraw it and create a new verified request.");
        if(request.State!="Approved"&&string.IsNullOrWhiteSpace(request.Reason))throw new ArgumentException("Record a client-facing reason for holding or rejecting the request.");
        int version=old.DecisionVersion+1;
        WorkflowStore.Sql(c,tx,"update studio_gallery_requests set state=$2,job_id=$3,client_id=$4,verification_reference=$5,reason=$6,decision_version=$7 where id=$1",
            id,request.State,request.JobId,request.ClientId,request.VerificationReference.Trim(),request.Reason.Trim(),version);
        var payload=new{state=request.State,jobId=request.JobId,clientId=request.ClientId,entitlementVerified=request.State=="Approved",reason=request.Reason.Trim(),version};
        Queue(c,tx,id,"Decision",Guid.NewGuid(),payload);
        if(request.State!="Approved"&&hasTransfers&&old.State=="Approved")
        {
            Guid withdrawal=Guid.NewGuid();int throughVersion=Convert.ToInt32(WorkflowStore.Scalar(c,tx,"select coalesce(max(version),0) from studio_gallery_transfers where request_id=$1",id));var revoke=new{id=withdrawal,requestId=id,galleryId=id,throughVersion,reason="Staff decision changed: "+request.Reason.Trim()};
            WorkflowStore.Sql(c,tx,"insert into studio_gallery_revocations(id,request_id,payload) values($1,$2,$3::jsonb)",withdrawal,id,JsonSerializer.Serialize(revoke,Json));
            WorkflowStore.Sql(c,tx,"update studio_gallery_transfers set state='Revocation requested' where request_id=$1",id);
            Queue(c,tx,id,"Revocation",withdrawal,revoke);
            Record(c,tx,id,old.JobId,"GalleryRevocationRequested",actor,JsonSerializer.Serialize(new{revocationId=withdrawal,reason="Staff decision changed"},Json));
        }
        Record(c,tx,id,request.JobId,"GalleryDecision",actor,JsonSerializer.Serialize(new{request.State,version,verificationReference=request.VerificationReference.Trim()},Json));
        tx.Commit();return new{id,state=request.State,decisionVersion=version};
    }
    public object Publish(Guid id,GalleryTransferCommand request,ClaimsPrincipal user)
    {
        AssertWrite(id,user);string actor=WorkflowStore.Name(user);using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        var r=Locked(c,tx,id);if(r.State!="Approved"||r.JobId is not Guid job)throw new ArgumentException("Approve and verify the client request first.");
        if(!workflow.CanManage(user,job)&&!access.HasPermission(user,job,"ManageProduction")&&!user.IsInRole("Administrator"))throw new UnauthorizedAccessException();
        int previous=Convert.ToInt32(WorkflowStore.Scalar(c,tx,"select coalesce(max(version),0) from studio_gallery_transfers where request_id=$1",id));
        if(previous!=request.ExpectedVersion)throw new ArgumentException("Gallery version changed. Refresh before publishing.");
        bool withdrawn=WorkflowStore.Exists(c,tx,"select exists(select 1 from studio_gallery_revocations where request_id=$1)",id);
        if(withdrawn&&!request.RepublishApproval)throw new ArgumentException("Explicitly approve a new publication after withdrawal.");
        string? title=WorkflowStore.Scalar(c,tx,"select title from studio_jobs where id=$1 and client_id=$2 and status in ('Ready for Delivery','Delivered','Completed')",job,r.ClientId) as string;
        if(title is null)throw new ArgumentException("Gallery delivery requires a ready job belonging to the verified client.");
        string? manifest=WorkflowStore.Scalar(c,tx,"select s.manifest::text from studio_workflow_publications p join studio_workflow_submissions s on s.id=p.submission_id where p.id=$1 and p.job_id=$2 and s.kind='Finished' and s.status='Approved'",request.PublicationId,job) as string;
        if(manifest is null)throw new ArgumentException("Choose an approved finished publication for this job.");
        var selected=JsonSerializer.Deserialize<JsonElement>(manifest).EnumerateArray().Select(x=>x.GetProperty("versionId").GetGuid()).ToArray();
        var files=new List<object>();
        using(var q=WorkflowStore.Command(c,tx,"select a.id,v.id,v.file_name,v.content_type,v.size_bytes,lower(v.sha256) from studio_asset_versions v join studio_media_assets a on a.job_id=v.job_id and a.asset_key=v.asset_key where v.job_id=$1 and v.id=any($2) and a.approved_version_id=v.id",job,selected))
        using(var reader=q.ExecuteReader())while(reader.Read())files.Add(new{itemId=reader.GetGuid(0),versionId=reader.GetGuid(1),fileName=reader.GetString(2),contentType=reader.GetString(3),sizeBytes=reader.GetInt64(4),sha256=reader.GetString(5)});
        if(files.Count==0||files.Count!=selected.Length)throw new ArgumentException("Every selected version must still be approved and available.");
        Guid transfer=Guid.NewGuid();int version=previous+1;
        var payload=new{id=transfer,requestId=id,galleryId=id,version,title,decisionVersion=r.DecisionVersion,republishApproval=request.RepublishApproval,items=files};
        string encoded=JsonSerializer.Serialize(payload,Json);
        WorkflowStore.Sql(c,tx,"insert into studio_gallery_transfers(id,request_id,version,manifest,created_by) values($1,$2,$3,$4::jsonb,$5)",transfer,id,version,encoded,actor);
        Queue(c,tx,id,"Transfer",transfer,payload);Record(c,tx,id,job,"GalleryPublicationRequested",actor,JsonSerializer.Serialize(new{transferId=transfer,version,fileCount=files.Count},Json));
        tx.Commit();return new{transferId=transfer,version,state="Pending synchronization"};
    }
    public object Revoke(Guid id,string reason,ClaimsPrincipal user)
    {
        AssertWrite(id,user);if(string.IsNullOrWhiteSpace(reason)||reason.Length>1000)throw new ArgumentException("Explain the revocation (up to 1000 characters).");
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();var r=Locked(c,tx,id);
        if(r.JobId is not Guid job||!workflow.CanManage(user,job)&&!access.HasPermission(user,job,"ManageProduction")&&!user.IsInRole("Administrator"))throw new UnauthorizedAccessException();
        if(r.State!="Approved")
        {
            using var prior=WorkflowStore.Command(c,tx,"select id,state from studio_gallery_revocations where request_id=$1 and payload->>'reason'=$2 order by created_at_utc desc limit 1",id,reason.Trim());
            using var reader=prior.ExecuteReader();if(reader.Read())return new{revocationId=reader.GetGuid(0),state=reader.GetString(1)};
        }
        Guid revocation=Guid.NewGuid();int throughVersion=Convert.ToInt32(WorkflowStore.Scalar(c,tx,"select coalesce(max(version),0) from studio_gallery_transfers where request_id=$1",id));var payload=new{id=revocation,requestId=id,galleryId=id,throughVersion,reason=reason.Trim()};
        WorkflowStore.Sql(c,tx,"insert into studio_gallery_revocations(id,request_id,payload) values($1,$2,$3::jsonb)",revocation,id,JsonSerializer.Serialize(payload,Json));
        WorkflowStore.Sql(c,tx,"update studio_gallery_requests set state='Held' where id=$1",id);
        WorkflowStore.Sql(c,tx,"update studio_gallery_transfers set state='Revocation requested' where request_id=$1",id);
        Queue(c,tx,id,"Revocation",revocation,payload);Record(c,tx,id,job,"GalleryRevocationRequested",WorkflowStore.Name(user),JsonSerializer.Serialize(new{revocationId=revocation,reason=reason.Trim()},Json));
        tx.Commit();return new{revocationId=revocation,state="Pending synchronization"};
    }
    public void Retry(Guid id,ClaimsPrincipal user)
    {
        AssertWrite(id,user);using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        WorkflowStore.Sql(c,tx,"update studio_gallery_exchange_outbox set next_attempt_at_utc=now() where request_id=$1 and status='Pending'",id);
        Record(c,tx,id,null,"GalleryRetryRequested",WorkflowStore.Name(user),"{}");tx.Commit();
    }
    void AssertRead(Guid id,ClaimsPrincipal user)
    {
        using var c=database.OpenConnection();object? job=WorkflowStore.Scalar(c,null,"select job_id from studio_gallery_requests where id=$1",id);
        if(!workflow.IsFrontDesk(user)&&(job is not Guid j||!access.HasPermission(user,j,"ViewAttachments")))throw new UnauthorizedAccessException();
    }
    void AssertWrite(Guid id,ClaimsPrincipal user,Guid? target=null)
    {
        AssertRead(id,user);using var c=database.OpenConnection();
        Guid? job=target??WorkflowStore.Scalar(c,null,"select job_id from studio_gallery_requests where id=$1",id) as Guid?;
        if(!workflow.IsFrontDesk(user)&&(job is not Guid j||!workflow.CanManage(user,j)&&!access.HasPermission(user,j,"ManageProduction")))throw new UnauthorizedAccessException();
    }
    static (string State,int DecisionVersion,Guid? JobId,Guid? ClientId) Locked(NpgsqlConnection c,NpgsqlTransaction tx,Guid id)
    {
        using var q=WorkflowStore.Command(c,tx,"select state,decision_version,job_id,client_id from studio_gallery_requests where id=$1 for update",id);using var r=q.ExecuteReader();
        if(!r.Read())throw new KeyNotFoundException();return (r.GetString(0),r.GetInt32(1),r.IsDBNull(2)?null:r.GetGuid(2),r.IsDBNull(3)?null:r.GetGuid(3));
    }
    static void Queue(NpgsqlConnection c,NpgsqlTransaction tx,Guid request,string kind,Guid id,object payload)
        =>WorkflowStore.Sql(c,tx,"insert into studio_gallery_exchange_outbox(id,request_id,kind,payload) values($1,$2,$3,$4::jsonb)",id,request,kind,JsonSerializer.Serialize(payload,Json));
    static JsonElement ReadJson(NpgsqlConnection c,string sql,params object?[] values)
        =>JsonSerializer.Deserialize<JsonElement>((string)WorkflowStore.Scalar(c,null,sql,values)!);
}
