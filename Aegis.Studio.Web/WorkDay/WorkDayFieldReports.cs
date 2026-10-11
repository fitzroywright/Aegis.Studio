using System.Text.Json;
using Npgsql;
namespace Aegis.Studio.Web.WorkDay;

public sealed record WorkDayFieldTarget(string TargetId,string Scope,string Name,string SourceFingerprint,long Version,string? Status,string? Collection=null,string? Reference=null,string? Instructions=null);
public sealed record WorkDayFieldStatus(string TargetId,string Status,string SourceFingerprint,long ExpectedTargetVersion,string? Reason=null);
public static class WorkDayFieldReports
{
    public const string Schema="""
        create table if not exists studio_workday_field_status(
          job_id uuid not null references studio_jobs(id),assignment_id uuid not null references studio_schedule_entries(id),target_id text not null,
          status text not null,source_fingerprint text not null,version bigint not null,reported_by text not null,
          occurred_at_utc timestamptz not null,operation_id uuid not null,
          primary key(job_id,target_id));
        create table if not exists studio_workday_field_conflicts(
          operation_id uuid primary key,assignment_id uuid not null references studio_schedule_entries(id),
          user_name text not null,fingerprint text not null,target_id text not null,report jsonb not null,
          message text not null,received_at_utc timestamptz not null default now());
        create table if not exists studio_workday_session_subjects(
          assignment_id uuid not null references studio_schedule_entries(id),subject_id uuid not null references studio_job_subjects(id),
          primary key(assignment_id,subject_id));
        create or replace view studio_workday_roster_progress as
          select s.id,s.job_id,s.collection_name,
            coalesce(case when f.source_fingerprint=md5(jsonb_build_array(to_jsonb(s),array(select x.asset_id from studio_media_asset_subjects x where x.subject_id=s.id order by x.asset_id))::text) then
              case f.status when 'Completed' then 'Photographed' when 'NoShow' then 'Absent' else f.status end end,
              case when s.production_status='Not Photographed' and exists(select 1 from studio_media_asset_subjects x where x.subject_id=s.id) then 'Photographed' else s.production_status end) as status
          from studio_job_subjects s left join studio_workday_field_status f on f.job_id=s.job_id and f.target_id='subject:'||s.id
          where not s.is_system;
        """;
    // Fingerprints cover the Studio data reviewed on the device. A field report
    // does not overwrite Job lifecycle or production approval state.
    private const string TargetsSql="""
        with job as(select j.* from studio_jobs j join studio_schedule_entries e on e.job_id=j.id where e.id=$1),
        collections as(
          select s.collection_name as name from studio_job_subjects s join job j on j.id=s.job_id where not s.is_system
          union select c.name from studio_media_collections c join job j on j.id=c.job_id),
        targets as(
          select 'session:'||e.id as target_id,'Session' as scope,e.title as name,
            md5(to_jsonb(e)::text) as fingerprint,null::text as collection,null::text as reference,e.notes as instructions from studio_schedule_entries e where e.id=$1
          union all
          select 'subject:'||s.id,'Roster',s.name,md5(jsonb_build_array(to_jsonb(s),array(select x.asset_id from studio_media_asset_subjects x where x.subject_id=s.id order by x.asset_id))::text),s.collection_name,s.reference,concat_ws(E'\n',nullif(s.notes,''),nullif(s.photo_notes,''))
            from studio_job_subjects s join job j on j.id=s.job_id where not s.is_system and (not exists(select 1 from studio_workday_session_subjects where assignment_id=$1) or exists(select 1 from studio_workday_session_subjects x where x.assignment_id=$1 and x.subject_id=s.id))
          union all
          select 'collection:'||md5(c.name),'Collection',c.name,
            md5(jsonb_build_array(c.name,coalesce(jsonb_agg(to_jsonb(s) order by s.id) filter(where s.id is not null),'[]'::jsonb))::text)
             ,c.name,null::text,null::text
            from collections c left join studio_job_subjects s on s.job_id=(select id from job) and s.collection_name=c.name and not s.is_system group by c.name)
        select t.target_id,t.scope,t.name,t.fingerprint,coalesce(f.version,0),case when f.source_fingerprint=t.fingerprint then f.status else (select p.status from studio_workday_roster_progress p where 'subject:'||p.id=t.target_id) end,t.collection,t.reference,t.instructions
          from targets t left join studio_workday_field_status f on f.job_id=(select id from job) and f.target_id=t.target_id
          order by t.scope,t.name
        """;
    public static IReadOnlyList<WorkDayFieldTarget> Targets(NpgsqlConnection c,NpgsqlTransaction? tx,Guid assignment)
    {
        using var q=new NpgsqlCommand(TargetsSql,c,tx);q.Parameters.AddWithValue(assignment);
        using var r=q.ExecuteReader();var rows=new List<WorkDayFieldTarget>();
        while(r.Read())rows.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetInt64(4),r.IsDBNull(5)?null:r.GetString(5),r.IsDBNull(6)?null:r.GetString(6),r.IsDBNull(7)?null:r.GetString(7),r.IsDBNull(8)?null:r.GetString(8)));
        return rows;
    }
    public static WorkDayFieldStatus Parse(string? payload)
    {
        WorkDayFieldStatus? value;
        try{value=JsonSerializer.Deserialize<WorkDayFieldStatus>(payload??"",new JsonSerializerOptions(JsonSerializerDefaults.Web));}
        catch(JsonException){throw new InvalidOperationException("A valid field-status report is required.");}
        if(value is null||value.ExpectedTargetVersion<0||string.IsNullOrWhiteSpace(value.TargetId)||value.TargetId.Length>100||value.SourceFingerprint?.Length!=32||
           value.Status is not ("Photographed" or "Absent" or "NotStarted" or "InProgress" or "Completed" or "Cancelled" or "Deferred" or "NoShow"))
            throw new InvalidOperationException("Choose a valid session, collection or roster status and retain its reviewed revision.");
        if((value.Status is "Cancelled" or "Deferred" or "NoShow" or "Absent")&&string.IsNullOrWhiteSpace(value.Reason))
            throw new InvalidOperationException("A reason is required for cancellation, deferral or no-show reports.");
        if((value.Reason?.Length??0)>3000)throw new InvalidOperationException("Field reasons may contain at most 3000 characters.");
        return value;
    }
    public static string? Apply(NpgsqlConnection c,NpgsqlTransaction tx,Guid assignment,WorkDayFieldStatus report,string actor,Guid operation,DateTimeOffset occurred)
    {
        // The caller holds Job and schedule locks; lock roster rows against Studio edits.
        using(var q=new NpgsqlCommand("select s.id from studio_job_subjects s join studio_schedule_entries e on e.job_id=s.job_id where e.id=$1 order by s.id for update of s",c,tx))
        {q.Parameters.AddWithValue(assignment);using var r=q.ExecuteReader();while(r.Read()){} }
        var target=Targets(c,tx,assignment).SingleOrDefault(x=>x.TargetId==report.TargetId);
        if(target is null)return "This roster or collection is no longer in the assigned Job. Review the retained report.";
        if(target.Version!=report.ExpectedTargetVersion||target.SourceFingerprint!=report.SourceFingerprint)
            return "Studio has newer Job, roster or field-status changes. Refresh and review this retained report before submitting a new update.";
        using var write=new NpgsqlCommand("insert into studio_workday_field_status(job_id,assignment_id,target_id,status,source_fingerprint,version,reported_by,occurred_at_utc,operation_id) select e.job_id,$1,$2,$3,$8,$4,$5,$6,$7 from studio_schedule_entries e where e.id=$1 on conflict(job_id,target_id) do update set assignment_id=excluded.assignment_id,status=excluded.status,source_fingerprint=excluded.source_fingerprint,version=excluded.version,reported_by=excluded.reported_by,occurred_at_utc=excluded.occurred_at_utc,operation_id=excluded.operation_id",c,tx);
        write.Parameters.AddWithValue(assignment);write.Parameters.AddWithValue(report.TargetId);write.Parameters.AddWithValue(report.Status);write.Parameters.AddWithValue(checked(target.Version+1));write.Parameters.AddWithValue(actor);write.Parameters.AddWithValue(occurred);write.Parameters.AddWithValue(operation);write.Parameters.AddWithValue(report.SourceFingerprint);write.ExecuteNonQuery();return null;
    }
}
