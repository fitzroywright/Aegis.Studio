using System.Text.Json;
namespace Aegis.Studio.Web.Crm;

public sealed partial class CrmStore
{
    public JsonElement Section(Guid client,string section,int page=1)
    {
        access.Client(client);int offset=(Math.Clamp(page,1,100000)-1)*50;
        string sql=section switch
        {
            "jobs"=>"""
                select j.id,j.title,j.status,j.event_date "eventDate",j.created_at_utc "createdAtUtc",j.workspace_id "workspaceId",w.name workspace,j.job_lead "assignedTo"
                from studio_jobs j join studio_workspaces w on w.id=j.workspace_id where j.client_id=$1 and j.id=any($4)
                """,
            "sessions"=>"""
                select s.id,s.job_id "jobId",j.title,s.photographer_user_name "photographer",s.assistant_user_name "assistant",s.call_time_utc "startsAtUtc",s.planned_end_utc "endsAtUtc",s.status,s.location
                from studio_shoot_sessions s join studio_jobs j on j.id=s.job_id where j.client_id=$1 and j.id=any($4)
                """,
            "enquiries"=>"""
                select e.id,e.subject title,e.lead_status status,e.crm_metadata metadata,e.job_id "jobId",e.workspace_id "workspaceId",e.created_at_utc "createdAtUtc",e.channel
                from studio_conversations e where e.client_id=$1 and ($2 or e.workspace_id=any($3)) and (e.job_id is null or e.job_id=any($4))
                """,
            "galleries"=>"""
                select p.id,p.job_id "jobId",j.title,p.created_at_utc "createdAtUtc",p.provider,p.reference,'Publication handoff recorded' status
                from studio_workflow_publications p join studio_jobs j on j.id=p.job_id where j.client_id=$1 and j.id=any($4)
                """,
            "documents"=>"""
                select a.id,j.id "jobId",j.title,a.file_name "fileName",a.content_type "contentType",a.created_at_utc "createdAtUtc"
                from studio_attachments a join studio_jobs j on j.conversation_id=a.conversation_id where j.client_id=$1 and j.id=any($4)
                """,
            "communications"=>"""
                select m.id,e.job_id "jobId",e.id "enquiryId",e.subject title,e.channel,m.author_display_name sender,e.contact_address recipient,m.body,m.created_at_utc "createdAtUtc",'Recorded inbound / conversation' status,null::text "lastError"
                from studio_messages m join studio_conversations e on e.id=m.conversation_id where e.client_id=$1 and m.visibility='External' and ($2 or e.workspace_id=any($3)) and (e.job_id is null or e.job_id=any($4))
                union all
                select o.id,e.job_id,e.id,o.subject,o.delivery_channel,o.from_address,o.to_address,o.body,o.created_at_utc,o.status,o.last_error
                from studio_outbound_email o join studio_conversations e on e.id=o.conversation_id where e.client_id=$1 and ($2 or e.workspace_id=any($3)) and (e.job_id is null or e.job_id=any($4))
                union all
                select a.id,null::uuid,null::uuid,a.event_type,case when a.event_type='Call recorded' then 'Telephone' else 'Internal' end,a.actor,'Studio',a.description,a.created_at_utc,'Manually recorded',null::text
                from studio_crm_activity a where a.client_id=$1 and a.event_type in ('Internal note','Call recorded') and ($2 or a.workspace_id=any($3) or a.workspace_id is null)
                union all
                select m.id,j.id,null::uuid,t.title,'Studio',m.sender_user_name,'Job conversation',m.body,m.created_at_utc,'Recorded in job conversation',null::text
                from studio_inapp_messages m join studio_message_threads t on t.id=m.thread_id and t.scope_type='Job' join studio_jobs j on j.id=t.scope_id where j.client_id=$1 and j.id=any($4)
                """,
            "activity"=>"""
                select a.id,a.event_type "eventType",a.description,a.actor,a.related_id "relatedId",null::uuid "jobId",a.created_at_utc "createdAtUtc" from studio_crm_activity a
                where a.client_id=$1 and ($2 or a.workspace_id=any($3) or a.workspace_id is null)
                union all
                select a.id,a.action,a.detail,a.user_name,a.activity_id,a.job_id,a.created_at_utc from studio_operational_audit a join studio_jobs j on j.id=a.job_id where j.client_id=$1 and j.id=any($4) and (($8 and j.id=any($9)) or a.action !~* '(invoice|payment|deposit|refund|finance|credit|price|charge|quote|balance|receipt|pos|discount)')
                union all
                select j.id,'JobCreated',j.title,coalesce(j.job_lead,'Studio'),j.id,j.id,j.created_at_utc from studio_jobs j where j.client_id=$1 and j.id=any($4)
                union all
                select e.id,'EnquiryReceived',e.subject,'Studio',e.id,e.job_id,e.created_at_utc from studio_conversations e where e.client_id=$1 and ($2 or e.workspace_id=any($3)) and (e.job_id is null or e.job_id=any($4))
                union all
                select s.id,'SessionScheduled',j.title,s.photographer_user_name,s.id,s.job_id,s.created_at_utc from studio_shoot_sessions s join studio_jobs j on j.id=s.job_id where j.client_id=$1 and j.id=any($4)
                union all
                select s.id,'SessionCompleted',j.title,s.photographer_user_name,s.id,s.job_id,s.checked_out_at_utc from studio_shoot_sessions s join studio_jobs j on j.id=s.job_id where j.client_id=$1 and j.id=any($4) and s.checked_out_at_utc is not null
                union all
                select p.id,'GalleryPublished',p.reference,p.actor,p.id,p.job_id,p.created_at_utc from studio_workflow_publications p join studio_jobs j on j.id=p.job_id where j.client_id=$1 and j.id=any($4)
                union all
                select d.id,'DeliveryCompleted',d.method,d.actor,d.id,d.job_id,d.created_at_utc from studio_workflow_deliveries d join studio_jobs j on j.id=d.job_id where j.client_id=$1 and j.id=any($4)
                """,
            _=>throw new ArgumentException("Unknown profile section.")
        };
        string? permission=section switch{"communications"=>CrmPermissions.Communications,_=>null};if(permission is not null)access.Demand(permission);
        Guid[] jobs=AllowedJobs(section switch{"documents" or "galleries"=>"ViewAttachments","communications"=>"ViewConversation",_=>null});
        if(section=="activity")
        {
            if(access.Has(CrmPermissions.Communications))sql+="""

                union all select m.id,'MessageRecorded',e.subject,m.author_display_name,e.id,e.job_id,m.created_at_utc
                from studio_messages m join studio_conversations e on e.id=m.conversation_id where e.client_id=$1 and m.visibility='External' and ($2 or e.workspace_id=any($3)) and (e.job_id is null or e.job_id=any($7))
                union all select o.id,'Message'||o.status,o.subject,coalesce(o.sent_by_user_name,'Studio'),e.id,e.job_id,o.created_at_utc
                from studio_outbound_email o join studio_conversations e on e.id=o.conversation_id where e.client_id=$1 and ($2 or e.workspace_id=any($3)) and (e.job_id is null or e.job_id=any($7))
                """;
            if(access.Has(CrmPermissions.Financials))sql+="""

                union all select f.id,'FinancialRecord',f.kind,'Studio',f.id,f.job_id,f.created_at_utc from studio_financial_events f join studio_jobs j on j.id=f.job_id where j.client_id=$1 and j.id=any($9)
                union all select i.id,'InvoiceGenerated',i.invoice_number,i.created_by,i.id,i.job_id,i.created_at_utc from studio_account_invoices i join studio_jobs j on j.id=i.job_id where j.client_id=$1 and j.id=any($9)
                """;
        }
        // Consistent SQL parameters across projections, including those without workspace predicates.
        return Query("with scope as(select $1::uuid client,$2::boolean admin,$3::uuid[] workspaces,$4::uuid[] jobs,$7::uuid[] communication_jobs,$8::boolean finance,$9::uuid[] financial_jobs), rows as ("+sql+") select jsonb_build_object('items',coalesce((select jsonb_agg(to_jsonb(p)) from (select * from rows order by "+(section=="sessions"?"\"startsAtUtc\"":"\"createdAtUtc\"")+" desc,id limit 50 offset $5) p),'[]'::jsonb),'total',(select count(*) from rows),'page',$6)::text",client,access.IsAdministrator,access.Workspaces,jobs,offset,page,access.Has(CrmPermissions.Communications)?AllowedJobs("ViewConversation"):Array.Empty<Guid>(),access.Has(CrmPermissions.Financials),access.Has(CrmPermissions.Financials)?AllowedJobs("ViewFinancials"):Array.Empty<Guid>());
    }
    public JsonElement Financials(Guid client)
    {
        access.Client(client,CrmPermissions.Financials);return Query("""
            with jobs as(select * from studio_jobs where client_id=$1 and id=any($2)),
            receipts as(select e.* from studio_financial_events e join jobs j on j.id=e.job_id),
            invoices as(select distinct i.* from studio_account_invoices i where i.job_id in(select id from jobs) or exists(select 1 from studio_account_invoice_jobs x where x.invoice_id=i.id and x.job_id in(select id from jobs))),
            safe_invoices as(select i.* from invoices i where i.job_id=any($2) and not exists(select 1 from studio_account_invoice_jobs x join studio_jobs j on j.id=x.job_id where x.invoice_id=i.id and (j.client_id is distinct from $1 or not (j.id=any($2)))))
            select jsonb_build_object('currency','JMD','totalPaid',coalesce((select sum(amount) from receipts where lower(kind) in ('payment','deposit')),0),
            'lifetimeValue',coalesce((select sum(amount) from receipts where lower(kind) in ('payment','deposit','refund','reversal')),0),
            'totalInvoiced',coalesce((select sum(total_amount) from safe_invoices where status not in ('Draft','Void','Cancelled') and currency='JMD'),0),
            'outstandingBalance',coalesce((select sum(case when lower(kind) in ('charge','adjustment','discount') then amount when lower(kind) in ('payment','deposit','refund','reversal') then -amount else 0 end) from receipts),0),
            'completedJobs',(select count(*) from jobs where status='Completed'),'totalJobs',(select count(*) from jobs),
            'averageOrderValue',(select sum(amount)/nullif((select count(*) from jobs),0) from receipts where lower(kind) in ('payment','deposit','refund','reversal')),
            'lastPurchase',(select max(created_at_utc) from receipts where lower(kind) in ('payment','deposit') and amount>0),
            'repeatBookingDays',(select case when count(*)>1 then extract(epoch from max(created_at_utc)-min(created_at_utc))/86400/(count(*)-1) end from jobs),
            'combinedInvoicesExcluded',(select count(*) from invoices)-(select count(*) from safe_invoices),
            'quotes',coalesce((select jsonb_agg(jsonb_build_object('id',q.id,'title',q.payload->>'title','total',q.total,'status',q.status,'createdAtUtc',q.created_at_utc)) from studio_client_quotes q where q.client_id=$1 and (q.payload->>'workspaceId')::uuid=any($3)),'[]'::jsonb),
            'invoices',coalesce((select jsonb_agg(jsonb_build_object('id',id,'jobId',job_id,'invoiceNumber',invoice_number,'currency',currency,'totalAmount',total_amount,'status',status,'dueAtUtc',due_at_utc)) from safe_invoices),'[]'::jsonb),
            'ledger',coalesce((select jsonb_agg(to_jsonb(r)) from (select id,job_id "jobId",kind,amount,note,created_at_utc "createdAtUtc" from receipts order by created_at_utc desc limit 100) r),'[]'::jsonb),
            'rules','Amounts and job counts include accessible records only. JMD lifetime value = signed Payment + Deposit + Refund + Reversal entries. Balance = ledger charges less net receipts. Average order = net receipts / linked jobs. Combined invoices spanning clients are excluded. Invoice status never implies a recorded payment.')::text
            """,client,AllowedJobs("ViewFinancials"),access.Workspaces);
    }
}
