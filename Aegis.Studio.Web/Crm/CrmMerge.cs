using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
namespace Aegis.Studio.Web.Crm;

public sealed partial class CrmStore
{
    private static List<(string Table,string Column)> References(NpgsqlConnection c,NpgsqlTransaction tx)
    {
        using var q=new NpgsqlCommand("""
            select distinct r.relname,a.attname from pg_constraint fk join pg_class r on r.oid=fk.conrelid
            join pg_attribute a on a.attrelid=r.oid and a.attnum=any(fk.conkey)
            where fk.contype='f' and fk.confrelid='studio_clients'::regclass and r.relnamespace=(select relnamespace from pg_class where oid='studio_clients'::regclass)
            and r.relname not in ('studio_clients','studio_crm_contacts') order by r.relname,a.attname
            """,c,tx);using var reader=q.ExecuteReader();var rows=new List<(string,string)>();while(reader.Read())rows.Add((reader.GetString(0),reader.GetString(1)));return rows;
    }
    private static string Quote(string identifier)=>'"'+identifier.Replace("\"","\"\"")+'"';
    private string MergePlan(NpgsqlConnection c,NpgsqlTransaction tx,Guid target,Guid source)
    {
        var counts=new Dictionary<string,long>();foreach(var reference in References(c,tx))counts[reference.Table+"."+reference.Column]=Convert.ToInt64(Scalar(c,tx,"select count(*) from "+Quote(reference.Table)+" where "+Quote(reference.Column)+"=$1",source));
        counts["contact relationships"]=Convert.ToInt64(Scalar(c,tx,"select count(*) from studio_crm_contacts where client_id=$1 or person_id=$1",source));
        var identity=Scalar(c,tx,"select jsonb_agg(to_jsonb(c) order by c.id)::text from studio_clients c where id in ($1,$2)",target,source);
        string affected=JsonSerializer.Serialize(counts);string token=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target+":"+source+":"+identity+":"+affected)));
        return JsonSerializer.Serialize(new{targetId=target,sourceId=source,counts,token,policy="Target fields retained. Source identity and metadata retained as archived alias. Linked records move; audit snapshots are retained. Review external gallery account associations before merging."});
    }
    private void CanMerge(Guid target,Guid source)
    {
        access.Client(target,CrmPermissions.Merge);access.Client(source,CrmPermissions.Merge);if(target==source)throw new ArgumentException("Choose two different clients.");
        using var c=database.OpenConnection();using var q=new NpgsqlCommand("select exists(select 1 from studio_jobs where client_id in ($1,$2) and not (workspace_id=any($3))) or exists(select 1 from studio_conversations where client_id in ($1,$2) and not (workspace_id=any($3)))",c);
        q.Parameters.AddWithValue(target);q.Parameters.AddWithValue(source);q.Parameters.AddWithValue(access.Workspaces);if(!access.IsAdministrator&&q.ExecuteScalar() is true)throw new UnauthorizedAccessException();
    }
    public JsonElement PreviewMerge(Guid target,Guid source)
    {
        CanMerge(target,source);using var c=database.OpenConnection();using var tx=c.BeginTransaction();return JsonSerializer.Deserialize<JsonElement>(MergePlan(c,tx,target,source));
    }
    public void Merge(Guid target,CrmMergeRequest r)
    {
        CanMerge(target,r.SourceId);if(string.IsNullOrWhiteSpace(r.Reason)||r.Reason.Length>1000||string.IsNullOrWhiteSpace(r.PreviewToken))throw new ArgumentException("Review a merge preview and supply a reason.");
        using var c=database.OpenConnection();using var tx=c.BeginTransaction(System.Data.IsolationLevel.Serializable);
        if(Convert.ToInt64(Scalar(c,tx,"select count(*) from (select id from studio_clients where id in ($1,$2) and merged_into_id is null and archived_at_utc is null order by id for update) locked",target,r.SourceId))!=2)throw new ArgumentException("Choose two active, unmerged identities.");
        var plan=JsonSerializer.Deserialize<JsonElement>(MergePlan(c,tx,target,r.SourceId));if(plan.GetProperty("token").GetString()!=r.PreviewToken)throw new InvalidOperationException("Client relationships changed. Review a fresh merge preview.");
        // Explicitly consolidate relationship uniqueness before moving ordinary foreign keys.
        Execute(c,tx,"""
            insert into studio_crm_contacts(id,client_id,person_id,role,is_primary_billing,is_booking_contact)
            select gen_random_uuid(),case when client_id=$1 then $2 else client_id end,case when person_id=$1 then $2 else person_id end,role,is_primary_billing,is_booking_contact
            from studio_crm_contacts where (client_id=$1 or person_id=$1) and (case when client_id=$1 then $2 else client_id end)<>(case when person_id=$1 then $2 else person_id end)
            on conflict(client_id,person_id,role) do update set is_primary_billing=studio_crm_contacts.is_primary_billing or excluded.is_primary_billing,is_booking_contact=studio_crm_contacts.is_booking_contact or excluded.is_booking_contact
            """,r.SourceId,target);
        Execute(c,tx,"delete from studio_crm_contacts where client_id=$1 or person_id=$1",r.SourceId);
        foreach(var reference in References(c,tx))Execute(c,tx,"update "+Quote(reference.Table)+" set "+Quote(reference.Column)+"=$2 where "+Quote(reference.Column)+"=$1",r.SourceId,target);
        // JSON booking payloads are mutable associations; historical email audit JSON remains untouched.
        Execute(c,tx,"update studio_frontdesk_requests set payload=jsonb_set(payload,'{clientId}',to_jsonb($2::uuid)) where payload->>'clientId'=$1::text",r.SourceId,target);
        Execute(c,tx,"update studio_client_quotes set payload=jsonb_set(payload,'{clientId}',to_jsonb($2::uuid)) where payload->>'clientId'=$1::text",r.SourceId,target);
        Execute(c,tx,"update studio_finance_authorizations set client_id=$2 where client_id=$1",r.SourceId,target);
        Execute(c,tx,"update studio_clients set tags=array(select distinct unnest(tags||(select tags from studio_clients where id=$2))),updated_at_utc=now() where id=$1",target,r.SourceId);
        Execute(c,tx,"update studio_clients set merged_into_id=$2,archived_at_utc=now(),relationship_status='Archived',updated_at_utc=now() where id=$1",r.SourceId,target);
        Audit(c,tx,target,null,"ClientsMerged","Source "+r.SourceId+" retained as alias. Reason: "+r.Reason,Actor,r.SourceId);tx.Commit();
    }
}
