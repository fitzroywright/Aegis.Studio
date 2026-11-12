using System.Text.Json;
using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.SessionPlanning;

// Keep planning metadata beside the existing stable subject identity. Roster imports
// update the established subject columns and leave these attributes intact.
public sealed class SubjectPlanStore(StudioDatabase database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static void SaveStructure(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid job, JsonElement row)
    {
        bool hasCategory = row.TryGetProperty("planCategory", out var category);
        bool hasGroups = row.TryGetProperty("planGroups", out var groups);
        if (!hasCategory && !hasGroups) return;
        using var read = new NpgsqlCommand("select id,coalesce(attributes->'sessionPlan','null'::jsonb)::text from studio_job_subjects where job_id=$1 and lower(reference)=lower($2) and not is_system for update", connection, transaction);
        read.Parameters.AddWithValue(job); read.Parameters.AddWithValue(row.GetProperty("reference").GetString()!);
        Guid id; SubjectPlan current;
        using (var reader = read.ExecuteReader())
        {
            if (!reader.Read()) throw new KeyNotFoundException();
            id = reader.GetGuid(0);
            current = JsonSerializer.Deserialize<SubjectPlan>(reader.GetString(1), Json) ?? new(0, "Subjects", [], "", false, []);
        }
        string nextCategory = hasCategory ? string.IsNullOrWhiteSpace(category.GetString()) ? "Subjects" : category.GetString()!.Trim() : current.Category;
        var nextGroups = hasGroups ? SubjectPlanRules.ParseHierarchy(groups.GetString()!) : current.Groups;
        if (current.Category == nextCategory && current.Groups.SequenceEqual(nextGroups)) return;
        var next = current with { Revision = checked(current.Revision + 1), Category = nextCategory, Groups = nextGroups };
        // Roster structure edits do not create, clear or change shot approvals.
        using var write = new NpgsqlCommand("update studio_job_subjects set attributes=jsonb_set(coalesce(attributes,'{}'::jsonb),'{sessionPlan}',$2::jsonb,true),updated_at_utc=clock_timestamp() where id=$1", connection, transaction);
        write.Parameters.AddWithValue(id); write.Parameters.AddWithValue(JsonSerializer.Serialize(next, Json)); write.ExecuteNonQuery();
    }

    public SubjectPlan Save(Guid job, Guid subject, SubjectPlan request, string actor, bool canReview)
    {
        SubjectPlanRules.Validate(request);
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var read = new NpgsqlCommand("select coalesce(attributes->'sessionPlan','null'::jsonb)::text from studio_job_subjects where id=$1 and job_id=$2 and not is_system for update", connection, transaction);
        read.Parameters.AddWithValue(subject); read.Parameters.AddWithValue(job);
        string raw = read.ExecuteScalar() as string ?? throw new KeyNotFoundException();
        var previous = JsonSerializer.Deserialize<SubjectPlan>(raw, Json);
        if ((previous?.Revision ?? 0) != request.Revision) throw new InvalidOperationException("Session Plan changed. Reload before saving; your edits have not been applied.");
        // Changing approval or its evidence is a production-review operation.
        var oldApprovals = previous?.Shots.Where(s => s.Status == "Approved").ToArray() ?? [];
        var newApprovals = request.Shots.Where(s => s.Status == "Approved").ToArray();
        if (!canReview && JsonSerializer.Serialize(oldApprovals, Json) != JsonSerializer.Serialize(newApprovals, Json))
            throw new UnauthorizedAccessException("Production permission is required to approve or change approved shots.");
        foreach (var shot in request.Shots)
        {
            foreach (Guid asset in shot.AssetIds)
            {
                using var check = new NpgsqlCommand("select exists(select 1 from studio_media_assets a join studio_media_asset_subjects x on x.asset_id=a.id where a.id=$1 and a.job_id=$2 and x.subject_id=$3 and a.deleted_at_utc is null)", connection, transaction);
                check.Parameters.AddWithValue(asset); check.Parameters.AddWithValue(job); check.Parameters.AddWithValue(subject);
                if (!(bool)check.ExecuteScalar()!) throw new ArgumentException("Each selected photo must belong to this job and be assigned to this exact person or group.");
            }
            if (shot.ApprovedVersionId is Guid version)
            {
                using var check = new NpgsqlCommand("select exists(select 1 from studio_asset_versions v join studio_media_assets a on a.job_id=v.job_id and a.asset_key=v.asset_key where v.id=$1 and a.job_id=$2 and a.id=any($3) and a.deleted_at_utc is null)", connection, transaction);
                check.Parameters.AddWithValue(version); check.Parameters.AddWithValue(job); check.Parameters.AddWithValue(shot.AssetIds);
                if (!(bool)check.ExecuteScalar()!) throw new ArgumentException("The approved version must belong to a selected photograph.");
            }
        }
        // Identification requirements are copied per subject, so later structure edits cannot weaken them.
        var identityFields = previous?.IdentificationFields ?? [];
        if (request.IdentityConfirmed && identityFields.Any(f => f.Required && (request.Identification is null || !request.Identification.TryGetValue(f.Name, out var value) || string.IsNullOrWhiteSpace(value))))
            throw new ArgumentException("Complete the required identification fields before confirming identity.");
        var saved = request with { Revision = checked(request.Revision + 1), IdentificationFields = identityFields, SubjectKind = previous?.SubjectKind ?? request.SubjectKind };
        using var write = new NpgsqlCommand("update studio_job_subjects set attributes=jsonb_set(coalesce(attributes,'{}'::jsonb),'{sessionPlan}',$3::jsonb,true),updated_by=$4,updated_at_utc=clock_timestamp() where id=$1 and job_id=$2", connection, transaction);
        write.Parameters.AddWithValue(subject); write.Parameters.AddWithValue(job); write.Parameters.AddWithValue(JsonSerializer.Serialize(saved, Json)); write.Parameters.AddWithValue(actor); write.ExecuteNonQuery();
        using var audit = new NpgsqlCommand("insert into studio_operational_audit(id,job_id,action,user_name,detail,created_at_utc) values($1,$2,'SessionPlanUpdated',$3,$4,now())", connection, transaction);
        audit.Parameters.AddWithValue(Guid.NewGuid()); audit.Parameters.AddWithValue(job); audit.Parameters.AddWithValue(actor); audit.Parameters.AddWithValue($"Updated requirements for subject {subject:D}; revision {saved.Revision}."); audit.ExecuteNonQuery();
        transaction.Commit();
        return saved;
    }
}
