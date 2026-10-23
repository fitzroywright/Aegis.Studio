using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Production;
using Aegis.Studio.Web.Workflow;
using Common.Storage;
using Npgsql;

namespace Aegis.Studio.Web.Operations;

internal static class DemoDatasetFinalizer
{
    internal static void Complete(StudioDatabase database, IFileStorage storage, string actor, Action<DemoSeedProgress>? report)
    {
        report?.Invoke(new(99, "Validating demo", 0, 0, "Checking current workflows, ownership, subjects and stored photographs."));
        using var connection = database.OpenConnection();
        using (var transaction = connection.BeginTransaction())
        {
            Run("select set_config('studio.demo_seed','true',true)");
            Run("""
                update studio_jobs set status=case status
                  when 'New' then 'Pending' when 'Booking' then 'Pending' when 'Confirmed' then 'Needs Assignment'
                  when 'Needs Scheduling' then 'Awaiting Scheduling' when 'Photography' then 'Photography In Progress' when 'Shooting' then 'Photography In Progress'
                  when 'Shoot Complete' then 'Awaiting Upload' when 'In Production' then 'Active Production'
                  when 'Production' then 'Active Production' when 'QC' then 'Awaiting Approval'
                  when 'Printing' then 'Active Production' when 'Editing' then 'Active Production'
                  when 'Culling' then 'Active Production' when 'Retouching' then 'Active Production'
                  when 'Client Review' then 'Awaiting Approval' when 'Needs Review' then 'Awaiting Approval'
                  when 'Ready for Client' then 'Ready for Delivery' else status end;
                insert into studio_job_owner_assignments(id,job_id,assigned_to,assigned_by,status)
                  select md5('demo-owner:'||id)::uuid,id,job_lead,'demo-seed','Accepted' from studio_jobs where nullif(trim(job_lead),'') is not null;
                update studio_jobs set owner_assignment_id=md5('demo-owner:'||id)::uuid where nullif(trim(job_lead),'') is not null;
                insert into studio_media_galleries(id) select id from studio_jobs on conflict do nothing;
                insert into studio_media_collections(job_id,name) select id,'Unclassified' from studio_jobs on conflict do nothing;
                insert into studio_job_subjects(id,job_id,gallery_id,name,reference,notes,updated_by,collection_name,is_system)
                  select md5('demo-unclassified:'||id)::uuid,id,id,'Unclassified','','','demo-seed','Unclassified',true from studio_jobs
                  where not exists(select 1 from studio_job_subjects s where s.job_id=studio_jobs.id and s.is_system and lower(s.name)='unclassified');
                """);
            var jobs = new List<Guid>();
            using (var command = new NpgsqlCommand("select id from studio_jobs where not exists(select 1 from studio_job_template_instances t where t.job_id=studio_jobs.id) order by id", connection, transaction))
            using (var reader = command.ExecuteReader()) while (reader.Read()) jobs.Add(reader.GetGuid(0));
            Guid templateId;
            using (var command = new NpgsqlCommand("select id from studio_commercial_templates where kind='Job' and active order by definition->>'name' limit 1", connection, transaction))
                templateId = command.ExecuteScalar() is Guid id ? id : throw new InvalidOperationException("Demo Job Templates were not created.");
            var template = CommercialTemplateStore.Get(connection, transaction, templateId)!;
            foreach (Guid job in jobs) CommercialTemplateStore.ApplyNewJob(connection, transaction, job, template, Guid.NewGuid(), actor);
            Run("update studio_job_activities a set status='Completed',started_at_utc=now(),completed_at_utc=now() from studio_jobs j where a.job_id=j.id and j.status='Completed' and a.workflow_node_id is not null");
            transaction.Commit();
            void Run(string sql) { using var command = new NpgsqlCommand(sql, connection, transaction); command.ExecuteNonQuery(); }
        }
        using (var command = new NpgsqlCommand("select count(*) from studio_jobs where not(status=any($1))", connection))
        {
            command.Parameters.AddWithValue(WorkflowRules.Stages);
            if ((long)command.ExecuteScalar()! != 0) throw new InvalidOperationException("Demo contains unsupported Job stages.");
        }
        Check("select count(*) from studio_jobs j where j.job_lead is not null and not exists(select 1 from studio_job_owner_assignments a where a.id=j.owner_assignment_id and a.job_id=j.id)", "Demo ownership is incomplete.");
        Check("select count(*) from studio_jobs j where not exists(select 1 from studio_job_template_instances t where t.job_id=j.id)", "Demo template coverage is incomplete.");
        using (var command=new NpgsqlCommand("select j.id,j.title,count(m.asset_key)::int from studio_jobs j left join studio_media_assets m on m.job_id=j.id group by j.id,j.title order by j.id",connection))
        using (var reader=command.ExecuteReader())
            while(reader.Read())
            {
                Guid jobId=reader.GetGuid(0);
                string title=reader.GetString(1);
                int actual=reader.GetInt32(2);
                int expected=DemoFullStudioData.ExpectedPhotoCount(jobId,title);
                if(actual<expected)
                    throw new InvalidOperationException($"Demo job '{title}' has {actual} photographs; its local photo category provides {expected} unique photographs.");
            }
        Check("select count(*) from studio_media_assets m where not exists(select 1 from studio_asset_versions v where v.job_id=m.job_id and v.asset_key=m.asset_key and v.size_bytes>0)", "A demo photograph has no stored version.");
        using (var command = new NpgsqlCommand("select count(*) from studio_production_items where source_version_id is not null", connection))
            if ((long)command.ExecuteScalar()! == 0) throw new InvalidOperationException("Approved-photo production examples are missing.");
        var paths = new List<string>();
        using (var command = new NpgsqlCommand("select distinct stored_path from studio_asset_versions", connection))
        using (var reader = command.ExecuteReader()) while (reader.Read()) paths.Add(reader.GetString(0));
        var versions = new PrefixedFileStorage(storage, "VersionStore");
        foreach (string path in paths)
            if (versions.GetMetadataAsync(path).GetAwaiter().GetResult() is not { Length: > 0 })
                throw new InvalidOperationException("A generated demo photograph is missing from storage.");
        void Check(string sql, string error)
        { using var command = new NpgsqlCommand(sql, connection); if ((long)command.ExecuteScalar()! != 0) throw new InvalidOperationException(error); }
    }
}
