using Aegis.Studio.Web.Persistence;
using Npgsql;
namespace Aegis.Studio.Web.Crm;

// Called only by Studio's existing, guarded demo generator after its relational seed.
public static class CrmDemo
{
    public static void Seed(StudioDatabase database,string actor)
    {
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        using var q=new NpgsqlCommand("""
            update studio_clients c set
              primary_workspace_id=coalesce(c.primary_workspace_id,(select j.workspace_id from studio_jobs j where j.client_id=c.id order by j.created_at_utc,j.id limit 1)),
              client_since=coalesce((select min(j.created_at_utc)::date from studio_jobs j where j.client_id=c.id),current_date),
              client_type=case when display_name ilike '%family%' then 'Family / Household' when display_name ilike any(array['%college%','%academy%','%school%']) then 'School'
                when display_name ilike any(array['%corp%','%group%','%ltd%','%products%']) then 'Corporate' else c.client_type end,
              tags=array(select distinct unnest(c.tags||array['Demo','Photography'])),metadata=metadata||'{"address":"Kingston, Jamaica (fictional demo address)","preferredCommunication":"Email","communicationPreferences":{"emailAllowed":true},"customFields":{"demo":true}}'::jsonb;
            update studio_clients c set account_owner=(select m.user_name from studio_workspace_members m where m.workspace_id=c.primary_workspace_id and m.is_active and m.access_workspace order by m.user_name limit 1);
            insert into studio_clients(id,display_name,email,phone,client_type,primary_workspace_id,account_owner,relationship_status,tags,metadata)
              select md5('crm-demo:'||x.n)::uuid,x.name,x.email,x.phone,x.kind,w.id,
                (select m.user_name from studio_workspace_members m where m.workspace_id=w.id and m.is_active and m.access_workspace order by m.user_name limit 1),
                x.status,array['Demo','CRM'],jsonb_build_object('address',x.address,'preferredCommunication','Email','customFields',jsonb_build_object('engagement',x.engagement))
              from (values
                (1,'Hummingbird Preparatory School','office@hummingbird.example.invalid','+1 876 555 0181','School','Active','18 Portrait Lane, Kingston (fictional)','Annual graduation portraits'),
                (2,'Marigold Community Arts Trust','hello@marigold.example.invalid','+1 876 555 0182','Organisation','Active','7 Shutter Avenue, Mandeville (fictional)','Quarterly community events'),
                (3,'Nia Campbell','nia@campbell.example.invalid','+1 876 555 0183','Individual','Prospect','22 Frame Road, Portmore (fictional)','Graduation enquiry'),
                (4,'Campbell Household','family@campbell.example.invalid','+1 876 555 0184','Family / Household','Active','22 Frame Road, Portmore (fictional)','Annual family portrait'),
                (5,'Dahlia Grant','dahlia@grant.example.invalid','+1 876 555 0185','Individual','Active','9 Lens Crescent, Kingston (fictional)','School booking administrator'),
                (6,'Lignum Creative Limited','accounts@lignum.example.invalid','+1 876 555 0186','Corporate','Inactive','31 Studio Way, Montego Bay (fictional)','Recurring corporate headshots')
              ) x(n,name,email,phone,kind,status,address,engagement)
              cross join lateral(select id from studio_workspaces where is_enabled and system_key is distinct from 'unassigned' order by name,id limit 1) w on conflict(id) do nothing;
            insert into studio_crm_contacts(id,client_id,person_id,role,is_primary_billing,is_booking_contact) values
              (md5('crm-demo-contact:1')::uuid,md5('crm-demo:1')::uuid,md5('crm-demo:5')::uuid,'School Administrator',true,true),
              (md5('crm-demo-contact:2')::uuid,md5('crm-demo:4')::uuid,md5('crm-demo:3')::uuid,'Parent',true,true),
              (md5('crm-demo-contact:3')::uuid,md5('crm-demo:2')::uuid,md5('crm-demo:5')::uuid,'Booking Contact',false,true)
              on conflict do nothing;
            insert into studio_crm_tasks(id,client_id,workspace_id,assigned_to,task_type,due_at_utc,priority,notes,created_by)
              select md5('crm-demo-task:'||c.id)::uuid,c.id,c.primary_workspace_id,c.account_owner,
                case when c.client_type='School' then 'Confirm Booking' else 'General Follow-up' end,
                now()+case when c.client_type='School' then interval '-2 days' else interval '3 days' end,
                case when c.client_type='School' then 'High' else 'Normal' end,'Fictional demo relationship follow-up.',$1
              from studio_clients c where c.primary_workspace_id is not null and c.account_owner is not null and c.tags @> array['Demo'] and c.relationship_status<>'Inactive'
              on conflict(id) do nothing;
            insert into studio_crm_activity(id,client_id,workspace_id,event_type,description,actor)
              select md5('crm-demo-note:'||c.id)::uuid,c.id,c.primary_workspace_id,'Internal note',
              case when c.client_type='School' then 'Annual portraits: confirm class lists and guardian consent through the Job session plan.'
              when c.client_type='Family / Household' then 'Returning family: discuss available portrait packages before the next booking.'
              else 'Fictional demo client record. Existing jobs and payments remain the source of operational history.' end,$1
              from studio_clients c where c.tags @> array['Demo'] on conflict(id) do nothing;
            update studio_conversations e set lead_status=case when e.job_id is not null then 'Won' else 'New' end where e.client_id is not null;
            -- Seed events are historical data, demo recreation must not send notifications.
            update studio_crm_events set processed_at_utc=now() where processed_at_utc is null;
            """,c,tx);foreach(string statement in q.CommandText.Split(';',StringSplitOptions.RemoveEmptyEntries)){if(string.IsNullOrWhiteSpace(statement))continue;using var part=new NpgsqlCommand(statement,c,tx);if(statement.Contains("$1",StringComparison.Ordinal))part.Parameters.AddWithValue(actor);part.ExecuteNonQuery();}tx.Commit();
    }
}
