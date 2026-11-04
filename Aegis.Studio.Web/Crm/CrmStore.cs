using System.Security.Claims;
using System.Text.Json;
using Aegis.Studio.Web.Media;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Operations;
using Npgsql;

namespace Aegis.Studio.Web.Crm;

public sealed partial class CrmStore(StudioDatabase database,ClaimsPrincipal user)
{
    private readonly CrmAccess access=new(database,user);
    private string Actor=>CrmAccess.Name(user);
    public static void Migrate(StudioDatabase db)
    {
        using var stream=typeof(CrmStore).Assembly.GetManifestResourceStream("Aegis.Studio.Web.Crm.CrmSchema.sql")!;
        using var reader=new StreamReader(stream);using var c=db.OpenConnection();using var tx=c.BeginTransaction();
        int batchNumber=0;
        foreach(string batch in reader.ReadToEnd().Split("-- CRM batch",StringSplitOptions.RemoveEmptyEntries))
        {
            batchNumber++;
            try { Execute(c,tx,batch); }
            catch(PostgresException error) { throw new InvalidOperationException($"CRM migration batch {batchNumber} failed ({error.SqlState}): {error.MessageText}",error); }
        }
        tx.Commit();
    }
    private static object Db(object? value)=>value??DBNull.Value;
    private static int Execute(NpgsqlConnection c,NpgsqlTransaction tx,string sql,params object?[] values)=>MediaProductionStore.Execute(c,tx,sql,values.Select(Db).ToArray());
    private static object? Scalar(NpgsqlConnection c,NpgsqlTransaction tx,string sql,params object?[] values)=>MediaProductionStore.Scalar(c,tx,sql,values.Select(Db).ToArray());
    private JsonElement Query(string sql,params object?[] values)
    {
        using var c=database.OpenConnection();using var q=new NpgsqlCommand(sql,c);foreach(var value in values)q.Parameters.AddWithValue(Db(value));
        return JsonSerializer.Deserialize<JsonElement>((string)q.ExecuteScalar()!);
    }
    private static void Audit(NpgsqlConnection c,NpgsqlTransaction tx,Guid client,Guid? workspace,string type,string description,string actor,Guid? related=null)
        =>Execute(c,tx,"insert into studio_crm_activity(id,client_id,workspace_id,event_type,description,actor,related_id) values($1,$2,$3,$4,$5,$6,$7)",Guid.NewGuid(),client,workspace,type,description,actor,related);
    private IReadOnlyDictionary<Guid,JobPermissionSet>? permissionMap;
    private Guid[] AllowedJobs(string? permission=null)
    {
        permissionMap??=new JobAccessService(database).GetPermissionMap(user);
        var workspaces=access.Workspaces;
        using var c=database.OpenConnection();using var q=new NpgsqlCommand("select id from studio_jobs where $1 or workspace_id=any($2)",c);
        q.Parameters.AddWithValue(access.IsAdministrator);q.Parameters.AddWithValue(workspaces);using var reader=q.ExecuteReader();var scoped=new HashSet<Guid>();while(reader.Read())scoped.Add(reader.GetGuid(0));
        return permissionMap.Where(entry=>scoped.Contains(entry.Key)&&entry.Value.AccessJob&&entry.Value.ViewJob&&(permission switch {"ViewFinancials"=>entry.Value.ViewFinancials,"ViewAttachments"=>entry.Value.ViewAttachments,"ViewConversation"=>entry.Value.ViewConversation,_=>true})).Select(entry=>entry.Key).ToArray();
    }
    public object Capabilities()=>new{isAdministrator=access.IsAdministrator,sources=Aegis.Studio.Domain.Bookings.BookingSources.Selectable,permissions=CrmPermissions.All.Where(access.Has),types=CrmRules.Types,statuses=CrmRules.Statuses,taskTypes=CrmRules.TaskTypes,leadStatuses=CrmRules.LeadStatuses};
    private const string ClientJson=" " + """
        jsonb_build_object('id',c.id,'displayName',c.display_name,'email',c.email,'phone',c.phone,
        'clientType',c.client_type,'legalName',c.legal_name,'firstName',c.first_name,'lastName',c.last_name,
        'organisationName',c.organisation_name,'primaryWorkspaceId',c.primary_workspace_id,'accountOwner',c.account_owner,
        'relationshipStatus',c.relationship_status,'clientSince',c.client_since,'source',c.source,'tags',c.tags,
        'metadata',c.metadata,'archivedAtUtc',c.archived_at_utc,'mergedIntoId',c.merged_into_id,'updatedAtUtc',c.updated_at_utc)
        """ + " ";
    public JsonElement Get(Guid id){access.Client(id);return Query("select "+ClientJson+" from studio_clients c where id=$1",id);}
    public JsonElement Directory(CrmQuery request)
    {
        access.Demand(CrmPermissions.View);bool finance=access.Has(CrmPermissions.Financials);
        string order=" "+CrmRules.OrderBy(request.Sort,finance)+" ";
        int size=Math.Clamp(request.PageSize,1,200),page=Math.Clamp(request.Page,1,1000000);
        return Query("""
            with visible_jobs as (select * from studio_jobs where id=any($4)),
            j as (select client_id,count(*) total_jobs,max(created_at_utc) last_job from visible_jobs group by client_id),
            f as (select j.client_id,sum(e.amount) lifetime_value from visible_jobs j join studio_financial_events e on e.job_id=j.id
                  where j.id=any($5) and lower(e.kind) in ('payment','deposit','refund','reversal') group by j.client_id),
            t as (select client_id,min(due_at_utc) next_follow_up from studio_crm_tasks where status in ('Open','In Progress') and ($2 or workspace_id=any($3)) group by client_id),
            contact as (select e.client_id,max(m.created_at_utc) last_contact from studio_conversations e join studio_messages m on m.conversation_id=e.id
                        where ($2 or e.workspace_id=any($3)) and m.visibility='External' group by e.client_id),
            matching as (select
            """+ClientJson+"""
            ||jsonb_build_object('primaryWorkspace',w.name,'totalJobs',coalesce(j.total_jobs,0),'lastJob',j.last_job,
            'lifetimeValue',case when $6 then coalesce(f.lifetime_value,0) else null end,'lastContact',contact.last_contact,'nextFollowUp',t.next_follow_up) item,
            row_number() over(order by
            """+order+"""
            ) position from studio_clients c left join studio_workspaces w on w.id=c.primary_workspace_id
            left join j on j.client_id=c.id left join f on f.client_id=c.id left join t on t.client_id=c.id left join contact on contact.client_id=c.id
            where
            """+CrmAccess.Scope+"""
            and c.merged_into_id is null and ($1='' or studio_crm_search_document(c.display_name,c.email,c.phone,c.organisation_name,c.legal_name,c.first_name,c.last_name,c.metadata) @@ studio_crm_search_query($1) or lower(btrim(c.email))=lower($1) or nullif(studio_crm_normalize_phone(c.phone),'')=studio_crm_normalize_phone($1))
            and ($7='' or c.client_type=$7) and (($8='' and c.archived_at_utc is null) or c.relationship_status=$8)
            and ($9::uuid is null or c.primary_workspace_id=$9 or exists(select 1 from visible_jobs v where v.client_id=c.id and v.workspace_id=$9))
            and ($10='' or lower(c.account_owner)=lower($10))
            and ($11='' or ($11='overdue' and t.next_follow_up<now()) or ($11='upcoming' and t.next_follow_up>=now()) or ($11='none' and t.next_follow_up is null))
            and ($12::int is null or greatest(c.updated_at_utc,j.last_job,contact.last_contact)<now()-$12*interval '1 day'))
            select jsonb_build_object('items',coalesce((select jsonb_agg(item order by position) from matching where position>$13 and position<=$14),'[]'::jsonb),
            'total',(select count(*) from matching),'page',$15,'pageSize',$16)::text
            """,request.Search?.Trim()??"",access.IsAdministrator,access.Workspaces,AllowedJobs(),finance?AllowedJobs("ViewFinancials"):Array.Empty<Guid>(),finance,request.Type??"",request.Status??"",request.Workspace,request.Owner??"",request.FollowUp??"",request.InactiveDays,(page-1)*size,page*size,page,size);
    }
    public Guid Save(Guid? id,CrmClientRequest r)
    {
        CrmRules.Validate(r);if(r.RelationshipStatus=="Archived")access.Demand(CrmPermissions.Archive);access.Demand(id.HasValue?CrmPermissions.Edit:CrmPermissions.Create);if(id.HasValue)access.Client(id.Value,CrmPermissions.Edit);
        if(r.PrimaryWorkspaceId is {} workspace)access.Workspace(workspace);else if(!access.IsAdministrator)throw new ArgumentException("Choose an accessible primary workspace.");
        Guid client=id??Guid.NewGuid();using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        if(!string.IsNullOrWhiteSpace(r.AccountOwner)&&r.PrimaryWorkspaceId is {} scope&&Scalar(c,tx,"select user_name from studio_workspace_members where workspace_id=$1 and lower(user_name)=lower($2) and is_active and access_workspace",scope,r.AccountOwner) is null)throw new ArgumentException("Account owner must be an active member of the primary workspace.");
        if(id.HasValue&&Scalar(c,tx,"select id from studio_clients where id=$1 and merged_into_id is null for update",client) is null)throw new KeyNotFoundException();
        Execute(c,tx,"""
            insert into studio_clients(id,display_name,email,phone,client_type,legal_name,first_name,last_name,organisation_name,primary_workspace_id,account_owner,relationship_status,client_since,source,tags,metadata,archived_at_utc)
            values($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,coalesce($13::date,current_date),$14,$15,$16::jsonb,case when $12='Archived' then now() end)
            on conflict(id) do update set display_name=excluded.display_name,email=excluded.email,phone=excluded.phone,client_type=excluded.client_type,legal_name=excluded.legal_name,
            first_name=excluded.first_name,last_name=excluded.last_name,organisation_name=excluded.organisation_name,primary_workspace_id=excluded.primary_workspace_id,account_owner=excluded.account_owner,
            relationship_status=excluded.relationship_status,client_since=excluded.client_since,source=excluded.source,tags=excluded.tags,metadata=excluded.metadata,archived_at_utc=excluded.archived_at_utc,updated_at_utc=now()
            """,client,r.DisplayName.Trim(),string.IsNullOrWhiteSpace(r.Email)?null:r.Email.Trim(),r.Phone?.Trim(),r.ClientType,r.LegalName,r.FirstName,r.LastName,r.OrganisationName,r.PrimaryWorkspaceId,r.AccountOwner,r.RelationshipStatus,r.ClientSince,r.Source,r.Tags??[],r.Metadata?.GetRawText()??"{}");
        Audit(c,tx,client,r.PrimaryWorkspaceId,id.HasValue?"ClientUpdated":"ClientCreated",id.HasValue?"Client details updated.":"Client created.",Actor);
        tx.Commit();return client;
    }
    public void Archive(Guid id)
    {
        access.Client(id,CrmPermissions.Archive);using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        Execute(c,tx,"update studio_clients set archived_at_utc=now(),relationship_status='Archived',updated_at_utc=now() where id=$1 and merged_into_id is null",id);
        Audit(c,tx,id,null,"ClientArchived","Client archived; operational records retained.",Actor);tx.Commit();
    }
    public JsonElement Duplicates(Guid id)
    {
        access.Client(id);return Query("""
            select coalesce(jsonb_agg(item),'[]'::jsonb)::text from (select
            """+ClientJson+"""
            ||jsonb_build_object('reason',case when nullif(lower(btrim(c.email)),'')=lower(btrim(target.email)) then 'Matching email' when nullif(studio_crm_normalize_phone(c.phone),'')=studio_crm_normalize_phone(target.phone) then 'Matching telephone' else 'Name and organisation; review required' end) item
            from studio_clients c cross join studio_clients target where target.id=$1 and c.id<>target.id and c.merged_into_id is null and
            """+CrmAccess.Scope+"""
            and (nullif(lower(btrim(c.email)),'')=lower(btrim(target.email)) or nullif(studio_crm_normalize_phone(c.phone),'')=studio_crm_normalize_phone(target.phone)
            or (lower(c.display_name)=lower(target.display_name) and nullif(lower(c.organisation_name),'')=lower(target.organisation_name))) limit 50) d
            """,id,access.IsAdministrator,access.Workspaces);
    }
    public void Bulk(CrmBulkRequest r)
    {
        access.Demand(CrmPermissions.Edit);if(r.Ids is null||r.Tags is null||r.Ids.Length is <1 or >200||r.Tags.Length>40||r.Tags.Any(t=>string.IsNullOrWhiteSpace(t)||t.Length>60)||r.RelationshipStatus is not null&&!new[]{"Prospect","Active","Inactive"}.Contains(r.RelationshipStatus))throw new ArgumentException("Select up to 200 clients, valid tags and status.");
        foreach(var id in r.Ids)access.Client(id,CrmPermissions.Edit);
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        foreach(var id in r.Ids){Execute(c,tx,"update studio_clients set tags=array(select distinct unnest(tags||$2::text[])),relationship_status=coalesce($3,relationship_status),updated_at_utc=now() where id=$1 and merged_into_id is null",id,r.Tags,r.RelationshipStatus);Audit(c,tx,id,null,"ClientUpdated","Bulk tags/status updated.",Actor);}tx.Commit();
    }
    public JsonElement Contacts(Guid id)
    {
        access.Client(id);return Query("select coalesce(jsonb_agg("+ClientJson+"||jsonb_build_object('relationshipId',r.id,'role',r.role,'isPrimaryBilling',r.is_primary_billing,'isBookingContact',r.is_booking_contact)),'[]'::jsonb)::text from studio_crm_contacts r join studio_clients c on c.id=r.person_id where r.client_id=$1 and "+CrmAccess.Scope,id,access.IsAdministrator,access.Workspaces);
    }
    public void AddContact(Guid id,CrmContactRequest r)
    {
        access.Client(id,CrmPermissions.Edit);access.Client(r.PersonId);if(id==r.PersonId||string.IsNullOrWhiteSpace(r.Role)||r.Role.Length>100)throw new ArgumentException("Choose another person and a relationship role.");
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        Execute(c,tx,"insert into studio_crm_contacts(id,client_id,person_id,role,is_primary_billing,is_booking_contact) values($1,$2,$3,$4,$5,$6) on conflict(client_id,person_id,role) do update set is_primary_billing=excluded.is_primary_billing,is_booking_contact=excluded.is_booking_contact",Guid.NewGuid(),id,r.PersonId,r.Role,r.IsPrimaryBilling,r.IsBookingContact);
        Audit(c,tx,id,null,"ContactLinked",r.Role+" contact linked.",Actor,r.PersonId);tx.Commit();
    }
    public void RemoveContact(Guid id,Guid relationship)
    {
        access.Client(id,CrmPermissions.Edit);using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        if(Execute(c,tx,"delete from studio_crm_contacts where id=$1 and client_id=$2",relationship,id)!=1)throw new KeyNotFoundException();
        Audit(c,tx,id,null,"ContactUnlinked","Contact relationship removed.",Actor);tx.Commit();
    }
    public void Note(Guid id,CrmNoteRequest r)
    {
        access.Client(id,CrmPermissions.Edit);if(string.IsNullOrWhiteSpace(r.Body)||r.Body.Length>4000||!new[]{"Internal note","Call recorded"}.Contains(r.Kind))throw new ArgumentException("Enter a note of at most 4000 characters.");
        var client=Get(id);Guid? workspace=client.GetProperty("primaryWorkspaceId").ValueKind==JsonValueKind.String?client.GetProperty("primaryWorkspaceId").GetGuid():null;
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();Audit(c,tx,id,workspace,r.Kind,r.Body.Trim(),Actor);tx.Commit();
    }
    public JsonElement Views(){access.Demand(CrmPermissions.View);return Query("select coalesce(jsonb_agg(jsonb_build_object('id',id,'name',name,'definition',definition) order by name),'[]'::jsonb)::text from studio_crm_views where user_name=$1",Actor);}
    public void SaveView(CrmViewRequest r)
    {
        access.Demand(CrmPermissions.View);if(string.IsNullOrWhiteSpace(r.Name)||r.Name.Length>100||r.Definition.ValueKind!=JsonValueKind.Object||r.Definition.GetRawText().Length>5000)throw new ArgumentException("Supply a view name and filter object.");
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();Execute(c,tx,"insert into studio_crm_views values($1,$2,$3,$4::jsonb) on conflict(user_name,name) do update set definition=excluded.definition",Guid.NewGuid(),Actor,r.Name.Trim(),r.Definition.GetRawText());tx.Commit();
    }
    public void DeleteView(Guid id){access.Demand(CrmPermissions.View);using var c=database.OpenConnection();using var tx=c.BeginTransaction();Execute(c,tx,"delete from studio_crm_views where id=$1 and user_name=$2",id,Actor);tx.Commit();}
}
