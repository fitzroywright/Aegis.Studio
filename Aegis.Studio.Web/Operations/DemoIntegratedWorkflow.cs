using Aegis.Studio.Web.Production;
using Common.Storage;
using Npgsql;
namespace Aegis.Studio.Web.Operations;

// Scenario assembly for a freshly cleared demo dataset.
internal static class DemoIntegratedWorkflow
{
    internal static void Seed(NpgsqlConnection c,NpgsqlTransaction tx,string actor,IFileStorage storage,Action<DemoSeedProgress>? report)
    {
        using(var current=new NpgsqlCommand("select exists(select 1 from studio_state where key='demo-data-v7-integrated-workflow')",c,tx))if((bool)current.ExecuteScalar()!)return;
        report?.Invoke(new(97,"Connected workflow",0,0,"Linking shared workspaces, approved production, QC, payments and collection history."));
        Guid? approvalExample=null;string? approvalKey=null;Guid approvalJob=DemoDataExpansion.Id(41,101);
        using(var pick=new NpgsqlCommand("select a.asset_key from studio_media_assets a where a.job_id=$1 and a.approved_version_id is null and a.asset_key like 'demo-full/%' and not exists(select 1 from studio_asset_versions own where own.job_id=a.job_id and own.asset_key=a.asset_key and coalesce(own.demo_source->>'demo','false') <> 'true') and (select max(version_number) from studio_asset_versions v where v.job_id=a.job_id and v.asset_key=a.asset_key)<=2 order by a.id limit 1",c,tx))
        {pick.Parameters.AddWithValue(approvalJob);approvalKey=pick.ExecuteScalar() as string;}
        var versionStorage=new PrefixedFileStorage(storage,"VersionStore");string? exampleUrl=null;
        if(approvalKey is not null)
        {
            using var original=new NpgsqlCommand("select id,stored_path from studio_asset_versions where job_id=$1 and asset_key=$2 order by version_number limit 1",c,tx);original.Parameters.AddWithValue(approvalJob);original.Parameters.AddWithValue(approvalKey);Guid baseId;string path;using(var r=original.ExecuteReader()){r.Read();baseId=r.GetGuid(0);path=r.GetString(1);}
            var info=versionStorage.GetMetadataAsync(path).GetAwaiter().GetResult();info?.Metadata.TryGetValue("DemoSourceUrl",out exampleUrl);
            approvalExample=DemoDataExpansion.AddImage(c,tx,versionStorage,approvalJob,approvalKey,DemoDataExpansion.Id(89,101),3,0,baseId,actor,sourceUrl:exampleUrl);
        }
        foreach(int number in new[]{101,201,203,301,303,401,501,502,601,602,603,105,404})
        {
            Guid job=DemoDataExpansion.Id(41,number);
            Sql("update studio_jobs set status=case status when 'Production' then 'In Production' when 'QC' then 'In Production' when 'Printing' then 'In Production' when 'Editing' then 'Changes Requested' when 'Culling' then 'Changes Requested' when 'Retouching' then 'Changes Requested' when 'Client Review' then 'Needs Review' when 'Ready for Delivery' then 'Ready for Client' else status end where id=$1",job);
            Sql("insert into studio_job_workspace_links(job_id,workspace_id,added_by) select id,$2,$3 from studio_jobs where id=$1 and exists(select 1 from studio_workspaces where id=$2) on conflict do nothing",job,DemoDataExpansion.Id(1,number==201?1:4),actor);
            string? status;
            using(var get=new NpgsqlCommand("select status from studio_jobs where id=$1",c,tx)){get.Parameters.AddWithValue(job);status=get.ExecuteScalar() as string;}
            if(status is not ("In Production" or "Ready for Client" or "Completed"))continue;
            // Each order retains its source version even when a later revision is uploaded.
            Sql("update studio_media_assets a set approved_version_id=(select id from studio_asset_versions v where v.job_id=a.job_id and v.asset_key=a.asset_key order by v.version_number desc limit 1) where a.job_id=$1 and a.approved_version_id is null and a.asset_key like 'demo-full/%' and (select v.demo_source->>'demo' from studio_asset_versions v where v.job_id=a.job_id and v.asset_key=a.asset_key order by version_number desc limit 1)='true'",job);
            Guid activity=DemoDataExpansion.Id(80,number);
            Sql("""
                insert into studio_job_activities(id,job_id,activity_type,title,description,status,priority,assigned_to,created_by,created_at_utc,updated_at_utc)
                select $2,id,'QC','Approved production batch — quality control','Use only the pinned approved source; inspect, reject with a reason, rework and pass before customer handoff.',
                  case when status in ('Ready for Client','Completed') then 'Completed' else 'InProgress' end,'Normal',$3,$3,now(),now()
                from studio_jobs where id=$1 on conflict do nothing
                """,job,activity,actor);
            Sql("""
                insert into studio_production_items(id,activity_id,asset_id,source_version_id,product_code,quantity,completed_quantity,qc_state,qc_reason,qc_by,qc_at)
                select md5('demo-production:'||a.id::text)::uuid,$2,a.id,a.approved_version_id,'Approved photo print',1,
                  case when $3 or row_number() over(order by a.id)%3<>0 then 1 else 0 end,
                  case when $3 or row_number() over(order by a.id)%3<>0 then 'Passed' else 'Pending' end,null,$4,now()
                from studio_media_assets a where a.job_id=$1 and a.approved_version_id is not null and a.asset_key like 'demo-full/%' and exists(select 1 from studio_asset_versions v where v.id=a.approved_version_id and v.demo_source->>'demo'='true') on conflict do nothing
                """,job,activity,status is "Ready for Client" or "Completed",actor);

            Sql("insert into studio_operational_audit(id,job_id,action,user_name,detail,created_at_utc) values($1,$2,'ApprovedProductionBatch',$3,'Demo approved sources and production items linked to the existing QC activity.',now()) on conflict do nothing",DemoDataExpansion.Id(81,number),job,actor);
            if(status=="Completed")Sql("insert into studio_job_fulfillment(operation_id,job_id,method,recipient,recorded_by) select $1,id,'Collected',client_name,$3 from studio_jobs where id=$2 on conflict do nothing",DemoDataExpansion.Id(82,number),job,actor);
        }
        if(approvalExample.HasValue&&approvalKey is not null)DemoDataExpansion.AddImage(c,tx,versionStorage,approvalJob,approvalKey,DemoDataExpansion.Id(90,101),4,0,approvalExample,actor,sourceUrl:exampleUrl);
        // An actual POS sale and partial refund, referenced by the same Finance events.
        Guid sale=DemoDataExpansion.Id(83,101),jobId=DemoDataExpansion.Id(41,101),line=DemoDataExpansion.Id(84,101),refund=DemoDataExpansion.Id(86,101);
        Sql("""
            insert into studio_pos_sales(id,reference_type,reference_id,created_at_utc,submitted_at_utc,completed_at_utc,cashier_user_id,terminal_id,status,financial_event_posted)
            select $1,'StudioJob',id::text,now()-interval '2 days',now()-interval '2 days',now()-interval '2 days',$3,'StudioWeb',2,true from studio_jobs where id=$2 on conflict do nothing
            """,sale,jobId,actor);
        using(var exists=new NpgsqlCommand("select exists(select 1 from studio_jobs where id=$1)",c,tx)){exists.Parameters.AddWithValue(jobId);if(!(bool)exists.ExecuteScalar()!){Sql("insert into studio_state(key,value,updated_at_utc) values('demo-data-v7-integrated-workflow',$1,now()) on conflict do nothing",actor);return;}}
        Sql("insert into studio_pos_sale_lines(id,sale_id,code,description,unit_price,quantity) values($1,$2,'PRINT-5X7','5x7 approved portrait print',1500,3) on conflict do nothing",line,sale);
        Sql("insert into studio_pos_tenders(id,sale_id,kind,amount,reference) values($1,$2,0,4500,'DEMO verified cash receipt') on conflict do nothing",DemoDataExpansion.Id(85,101),sale);
        Sql("insert into studio_pos_refunds(id,sale_id,reason,created_at_utc) values($1,$2,'One extra print cancelled by customer',now()-interval '1 day') on conflict do nothing",refund,sale);
        Sql("insert into studio_pos_refund_lines(id,refund_id,sale_line_id,unit_price,quantity) values($1,$2,$3,1500,1) on conflict do nothing",DemoDataExpansion.Id(87,101),refund,line);
        Sql("insert into studio_pos_refund_tenders(id,refund_id,kind,amount,reference) values($1,$2,0,1500,'DEMO cash refund') on conflict do nothing",DemoDataExpansion.Id(88,101),refund);
        Sql("insert into studio_financial_events(id,job_id,kind,amount,note,created_at_utc,payment_method,recorded_by) values($1,$2,'POS Sale',4500,$3,now()-interval '2 days','Cash',$4),($5,$2,'POS Refund',-1500,$6,now()-interval '1 day','Cash',$4) on conflict do nothing",sale,jobId,$"Sale {sale}: Cash 4500",actor,refund,$"Refund {refund}: Cash 1500");
        Sql("insert into studio_pos_line_media(sale_line_id,asset_id,source_version_id) select $1,id,approved_version_id from studio_media_assets where job_id=$2 and approved_version_id is not null and asset_key like 'demo-full/%' order by id limit 1 on conflict do nothing",line,jobId);
        ProductionOrderLinker.Sync(c,tx,sale,actor);
        Sql("insert into studio_state(key,value,updated_at_utc) values('demo-data-v7-integrated-workflow',$1,now()) on conflict do nothing",actor);
        void Sql(string sql,params object?[] values){using var command=new NpgsqlCommand(sql,c,tx);foreach(var value in values)command.Parameters.AddWithValue(value??DBNull.Value);command.ExecuteNonQuery();}
    }
}
