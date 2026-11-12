using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.SessionPlanning;
using Npgsql;
namespace Aegis.Studio.Web.Operations;

internal static class DemoSessionPlans
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static void Seed(StudioDatabase database, string actor, Action<DemoSeedProgress>? report)
    {
        using var connection = database.OpenConnection();
        var jobs = DemoJobScope.Read(connection);
        bool schoolCreated = false;
        foreach (var job in jobs)
        {
            report?.Invoke(new(98, "Session Plans", 0, jobs.Count, "Creating configurable people, groups and photograph requirements for " + job.Title));
            using var transaction = connection.BeginTransaction();
            Run("select set_config('studio.demo_seed','true',true)");
            Run("insert into studio_media_galleries(id) values($1) on conflict do nothing", job.Id);
            bool large = DemoSessionPlanExamples.IsSchool(job.Title) && !schoolCreated;
            if (large) schoolCreated = true;
            var examples = DemoSessionPlanExamples.Build(job.Title, large);
            int index = 0;
            foreach (var person in examples)
            {
                SubjectPlanRules.Validate(person.Plan);
                Guid identity = new(SHA256.HashData(Encoding.UTF8.GetBytes($"demo-session-plan/{job.Id:D}/{person.Reference}")).AsSpan(0, 16));
                // Never manufacture photo identity links for newly generated people.
                // Empty references deliberately demonstrate missing coverage.
                string status = index++ % 17 == 0 ? "Absent" : "Not Photographed";
                Run("""
                    insert into studio_job_subjects(id,job_id,gallery_id,name,reference,collection_name,notes,updated_by,production_status,attributes,scheduled_at_utc,schedule_location)
                    values($1,$2,$2,$3,$4,$5,'Fictional configurable Session Plan example',$6,$7,jsonb_build_object('demo',true,'sessionPlan',$8::jsonb),date_trunc('day',now())+interval '3 days 14 hours'+($9::integer/30)*interval '30 minutes','Demo photography station')
                    on conflict(id) do update set name=excluded.name,collection_name=excluded.collection_name,attributes=coalesce(studio_job_subjects.attributes,'{}'::jsonb)||excluded.attributes,updated_at_utc=now()
                    """, identity, job.Id, person.Name, person.Reference, person.Collection, actor, status, JsonSerializer.Serialize(person.Plan, Json), index);
            }
            // Existing photographed subjects keep their original references and links.
            // Give them requirements backed by their actual assigned versions.
            var existing = new List<(Guid Id, string Collection, string Status)>();
            using (var read = new NpgsqlCommand("select id,coalesce(collection_name,'General'),production_status from studio_job_subjects where job_id=$1 and not is_system and attributes->'sessionPlan' is null order by id", connection, transaction))
            {
                read.Parameters.AddWithValue(job.Id);
                using var reader = read.ExecuteReader(); while (reader.Read()) existing.Add((reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));
            }
            int ordinal = 0;
            foreach (var subject in existing)
            {
                var photos = new List<(Guid Asset, Guid Version)>();
                using (var read = new NpgsqlCommand("select a.id,v.id from studio_media_asset_subjects x join studio_media_assets a on a.id=x.asset_id join lateral(select id from studio_asset_versions where job_id=a.job_id and asset_key=a.asset_key order by version_number desc limit 1)v on true where x.subject_id=$1 and a.deleted_at_utc is null order by a.id", connection, transaction))
                {
                    read.Parameters.AddWithValue(subject.Id);
                    using var reader = read.ExecuteReader(); while (reader.Read()) photos.Add((reader.GetGuid(0), reader.GetGuid(1)));
                }
                int sample = ordinal++ % 4;
                string state = photos.Count == 0 ? "Pending" : sample == 0 ? "Approved" : sample == 1 ? "Needs retake" : "Awaiting review";
                var shots = new[] { new PlannedShot("Primary photograph", state, photos.Select(p => p.Asset).Take(100).ToArray(), state == "Approved" ? photos[0].Version : null, state == "Needs retake" ? "Demo review: another capture is required." : "Fictional demo coverage example."), new PlannedShot("Alternative photograph", sample == 0 && photos.Count > 1 ? "Approved" : "Pending", photos.Skip(1).Take(100).Select(p => p.Asset).ToArray(), sample == 0 && photos.Count > 1 ? photos[1].Version : null) };
                var example = examples[0].Plan;
                var groups = DemoSessionPlanExamples.IsSchool(job.Title)
                    ? new PlanGroupLevel[] { new("Grade", "Grade 7"), new("Class", "7A") }
                    : new PlanGroupLevel[] { new(example.Groups[0].Name, subject.Collection) };
                var plan = new SubjectPlan(1, example.Category, groups, example.Role, state == "Approved", shots);
                SubjectPlanRules.Validate(plan);
                Run("update studio_job_subjects set attributes=jsonb_set(coalesce(attributes,'{}'::jsonb),'{sessionPlan}',$2::jsonb,true),updated_at_utc=now() where id=$1", subject.Id, JsonSerializer.Serialize(plan, Json));
            }
            Run("insert into studio_media_collections(job_id,name) select distinct job_id,collection_name from studio_job_subjects where job_id=$1 and not is_system and nullif(trim(collection_name),'') is not null on conflict do nothing", job.Id);
            transaction.Commit();
            void Run(string sql, params object[] values)
            {
                using var command = new NpgsqlCommand(sql, connection, transaction);
                foreach (var value in values) command.Parameters.AddWithValue(value);
                command.ExecuteNonQuery();
            }
        }
    }
}
