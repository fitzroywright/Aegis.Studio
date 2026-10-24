using System.Text.Json;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Workflow;
using Common.Storage;
namespace Aegis.Studio.Web.Intake;

// Publishes pinned, approved versions after the workflow transaction commits.
public sealed class GalleryPushWorker(StudioDatabase database,IFileStorage storage,IConfiguration configuration,ILogger<GalleryPushWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if(!configuration.GetValue<bool>("PublicSite:GalleryPushEnabled"))return;
        using var site=new PublicSiteClient(configuration);
        while(!ct.IsCancellationRequested)
        {
            try
            {
                using var c=database.OpenConnection();
                if((bool)WorkflowStore.Scalar(c,null,"select pg_try_advisory_lock(72919349)")!)try
                {
                    WorkflowStore.Sql(c,null,"create table if not exists studio_gallery_exports(publication_id uuid primary key,state text not null,last_error text,updated_at_utc timestamptz not null default now(),conversation_id uuid null references studio_conversations(id),job_id uuid null,client_id uuid null)");
                    WorkflowStore.Sql(c,null,"alter table studio_gallery_exports add column if not exists conversation_id uuid null references studio_conversations(id)");
                    WorkflowStore.Sql(c,null,"alter table studio_gallery_exports add column if not exists job_id uuid null");
                    WorkflowStore.Sql(c,null,"alter table studio_gallery_exports add column if not exists client_id uuid null");
                    var work=new List<(Guid Id,Guid Job,Guid Client,Guid Workspace,string ClientName,string ClientEmail,string Title,string Manifest)>();
                    using(var q=WorkflowStore.Command(c,null,"""
                      select p.id,p.job_id,j.client_id,j.workspace_id,coalesce(c.display_name,j.client_name),coalesce(c.email,''),j.title,s.manifest::text from studio_workflow_publications p
                      join studio_workflow_submissions s on s.id=p.submission_id join studio_jobs j on j.id=p.job_id
                      left join studio_clients c on c.id=j.client_id
                      left join studio_gallery_exports e on e.publication_id=p.id
                      where s.kind='Finished' and s.status='Approved' and j.client_id is not null and j.status in ('Ready for Delivery','Delivered','Completed')
                      and coalesce(e.state,'Pending') not in ('Published','Revoked','PendingReview') order by p.created_at_utc limit 10
                      """))using(var r=q.ExecuteReader())while(r.Read())work.Add((r.GetGuid(0),r.GetGuid(1),r.GetGuid(2),r.GetGuid(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetString(7)));
                    foreach(var p in work)
                    {
                        try
                        {
                            var media=new List<(Guid Version,string Name,string Path,string Type)>();
                            using(var q=WorkflowStore.Command(c,null,"select v.id,v.file_name,v.stored_path,v.content_type from jsonb_array_elements($2::jsonb) x join studio_asset_versions v on v.id=(x->>'versionId')::uuid and v.job_id=$1",p.Job,p.Manifest))using(var r=q.ExecuteReader())while(r.Read())media.Add((r.GetGuid(0),r.GetString(1),r.GetString(2),r.GetString(3)));
                            if(media.Count==0||media.Count!=JsonSerializer.Deserialize<JsonElement>(p.Manifest).GetArrayLength())throw new InvalidOperationException("Pinned media unavailable.");
                            var staged=await site.PostJson("/api/v1/galleries",new{id=p.Id,clientId=p.Client.ToString("D"),title=p.Title,finalDeliveryAvailable=true,items=media.Select(m=>new{versionId=m.Version,fileName=m.Name}).ToArray()},ct);
                            var stored=staged.TryGetProperty("storedVersionIds",out var versions)?versions.EnumerateArray().Select(x=>x.GetGuid()).ToHashSet():new HashSet<Guid>();
                            foreach(var m in media.Where(m=>!stored.Contains(m.Version))){await using var file=await storage.OpenReadAsync("VersionStore/"+m.Path,ct);await site.PutFile($"/api/v1/galleries/{p.Id:D}/media/{m.Version:D}",file,m.Type,ct);}
                            Guid conversation=Guid.NewGuid();
                            WorkflowStore.Sql(c,null,"""
                              insert into studio_conversations(id,workspace_id,subject,contact_name,contact_address,channel,is_closed,created_at_utc,client_id)
                              values($1,$2,$3,$4,$5,'Website Gallery',false,now(),$6)
                              on conflict(id) do nothing
                              """,conversation,p.Workspace,"Gallery update ready to publish — "+p.Title,p.ClientName,p.ClientEmail,p.Client);
                            WorkflowStore.Sql(c,null,"insert into studio_messages(id,conversation_id,author_display_name,visibility,body,created_at_utc,external_message_id) values($1,$2,'Studio','Internal',$3,now(),$4) on conflict do nothing",
                                Guid.NewGuid(),conversation,$"Approved photos from {p.Title} are staged for the client’s Public Site gallery. Review and publish from Front Desk.", "gallery-publication:"+p.Id);
                            WorkflowStore.Sql(c,null,"insert into studio_gallery_exports(publication_id,state,last_error,updated_at_utc,conversation_id,job_id,client_id) values($1,'PendingReview',null,now(),$2,$3,$4) on conflict(publication_id) do update set state='PendingReview',last_error=null,updated_at_utc=now(),conversation_id=coalesce(studio_gallery_exports.conversation_id,$2),job_id=$3,client_id=$4",
                                p.Id,conversation,p.Job,p.Client);
                            logger.LogInformation("Public gallery staged for Front Desk review. PublicationId={PublicationId} JobId={JobId} ConversationId={ConversationId}",p.Id,p.Job,conversation);
                        }
                        catch(Exception ex)when(ex is not OperationCanceledException)
                        {WorkflowStore.Sql(c,null,"insert into studio_gallery_exports values($1,'Failed',$2,now()) on conflict(publication_id) do update set state='Failed',last_error=$2,updated_at_utc=now()",p.Id,ex.GetType().Name);logger.LogWarning("Public gallery export retained for retry. PublicationId={PublicationId} ErrorType={ErrorType}",p.Id,ex.GetType().Name);}
                    }
                    var revoked=new List<Guid>();using(var q=WorkflowStore.Command(c,null,"select e.publication_id from studio_gallery_exports e join studio_workflow_publications p on p.id=e.publication_id join studio_workflow_submissions s on s.id=p.submission_id join studio_jobs j on j.id=p.job_id where e.state='Published' and (s.status<>'Approved' or j.status not in ('Ready for Delivery','Delivered','Completed'))"))using(var r=q.ExecuteReader())while(r.Read())revoked.Add(r.GetGuid(0));
                    foreach(var id in revoked){await site.Post($"/api/v1/galleries/{id:D}/revoke",new{},ct);WorkflowStore.Sql(c,null,"update studio_gallery_exports set state='Revoked',updated_at_utc=now() where publication_id=$1",id);}
                }finally{WorkflowStore.Sql(c,null,"select pg_advisory_unlock(72919349)");}
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception ex){logger.LogError("Public gallery synchronization failed. ErrorType={ErrorType}",ex.GetType().Name);}
            try{await Task.Delay(TimeSpan.FromSeconds(60),ct);}catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
        }
    }
}
