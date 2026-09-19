using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.WorkDay;

public sealed class WorkDayStore(StudioDatabase database)
{
    public void EnsureSchema()
    {
        using NpgsqlConnection c = database.OpenConnection();
        using NpgsqlCommand cmd = c.CreateCommand();
        cmd.CommandText = """
        create table if not exists studio_workday_operations(
          operation_id uuid primary key, user_name text not null, kind text not null,
          assignment_id uuid null, occurred_at_utc timestamptz not null, device_id text not null,
          text_value text null, category text null, expected_version bigint null,
          state text not null, result_message text null, server_version bigint null,
          received_at_utc timestamptz not null, acknowledged_at_utc timestamptz null);
        create index if not exists ix_workday_operations_user_time on studio_workday_operations(lower(user_name),occurred_at_utc desc);

        create table if not exists studio_workday_work_sessions(
          id uuid primary key, user_name text not null, started_at_utc timestamptz not null,
          ended_at_utc timestamptz null, device_id text not null, started_offline boolean not null,
          ended_offline boolean null, source text not null, updated_at_utc timestamptz not null);
        create unique index if not exists ux_workday_active_session on studio_workday_work_sessions(lower(user_name)) where ended_at_utc is null;

        create table if not exists studio_workday_location_observations(
          id uuid primary key, user_name text not null, work_session_id uuid null references studio_workday_work_sessions(id),
          assignment_id uuid null, operation_id uuid null references studio_workday_operations(operation_id),
          latitude double precision not null, longitude double precision not null, accuracy_metres double precision null,
          observed_at_utc timestamptz not null, source text null, mock_location boolean null, received_at_utc timestamptz not null);
        create index if not exists ix_workday_location_session on studio_workday_location_observations(work_session_id,observed_at_utc);

        create table if not exists studio_workday_notes(
          id uuid primary key, operation_id uuid not null unique references studio_workday_operations(operation_id),
          user_name text not null, assignment_id uuid not null, note_type text not null,
          category text null, body text not null, occurred_at_utc timestamptz not null);

        create table if not exists studio_workday_assignment_state(
          assignment_id uuid primary key, status text not null, version bigint not null default 1,
          changed_by text not null, changed_at_utc timestamptz not null, last_operation_id uuid not null);
        """;
        cmd.ExecuteNonQuery();
    }

    public WorkDayWorkSession? GetActiveSession(string userName)
    {
        using NpgsqlConnection c=database.OpenConnection(); using NpgsqlCommand cmd=c.CreateCommand();
        cmd.CommandText="select id,user_name,started_at_utc,ended_at_utc,device_id,started_offline,ended_offline,source,updated_at_utc from studio_workday_work_sessions where lower(user_name)=lower($1) and ended_at_utc is null order by started_at_utc desc limit 1";
        cmd.Parameters.AddWithValue(userName);
        using NpgsqlDataReader r=cmd.ExecuteReader(); if(!r.Read()) return null;
        return new(r.GetGuid(0),r.GetString(1),r.GetFieldValue<DateTimeOffset>(2),r.IsDBNull(3)?null:r.GetFieldValue<DateTimeOffset>(3),r.GetString(4),r.GetBoolean(5),r.IsDBNull(6)?null:r.GetBoolean(6),null,null,r.GetString(7),r.GetFieldValue<DateTimeOffset>(8));
    }

    public WorkDayOperationResult Apply(string userName, WorkDayOperationRequest request)
    {
        if(request.OperationId==Guid.Empty) throw new InvalidOperationException("OperationId is required.");
        if(string.IsNullOrWhiteSpace(request.DeviceId)) throw new InvalidOperationException("DeviceId is required.");
        using NpgsqlConnection c=database.OpenConnection(); using NpgsqlTransaction tx=c.BeginTransaction();

        using(NpgsqlCommand existing=c.CreateCommand())
        {
            existing.Transaction=tx; existing.CommandText="select state,result_message,server_version,coalesce(acknowledged_at_utc,received_at_utc) from studio_workday_operations where operation_id=$1";
            existing.Parameters.AddWithValue(request.OperationId);
            using NpgsqlDataReader r=existing.ExecuteReader();
            if(r.Read())
            {
                WorkDayOperationResult prior=new(request.OperationId,Enum.Parse<WorkDayOperationState>(r.GetString(0)),r.GetFieldValue<DateTimeOffset>(3),r.IsDBNull(1)?null:r.GetString(1),r.IsDBNull(2)?null:r.GetInt64(2));
                tx.Commit(); return prior;
            }
        }

        DateTimeOffset now=DateTimeOffset.UtcNow;
        long? version=null;
        string message="Acknowledged";

        if(request.Kind is WorkDayOperationKind.WorkSignIn)
        {
            using NpgsqlCommand active=c.CreateCommand(); active.Transaction=tx;
            active.CommandText="select id from studio_workday_work_sessions where lower(user_name)=lower($1) and ended_at_utc is null limit 1"; active.Parameters.AddWithValue(userName);
            object? activeId=active.ExecuteScalar();
            if(activeId is not null) message="Work session already active.";
            else
            {
                Guid sessionId=Guid.NewGuid();
                using NpgsqlCommand ins=c.CreateCommand(); ins.Transaction=tx;
                ins.CommandText="insert into studio_workday_work_sessions(id,user_name,started_at_utc,device_id,started_offline,source,updated_at_utc) values($1,$2,$3,$4,$5,'mobile',now())";
                ins.Parameters.AddWithValue(sessionId);ins.Parameters.AddWithValue(userName);ins.Parameters.AddWithValue(request.OccurredAtUtc);ins.Parameters.AddWithValue(request.DeviceId);ins.Parameters.AddWithValue(request.Text=="offline");ins.ExecuteNonQuery();
                SaveLocation(c,tx,userName,sessionId,request,null);
            }
        }
        else if(request.Kind is WorkDayOperationKind.WorkSignOut)
        {
            Guid? sessionId=null;
            using(NpgsqlCommand find=c.CreateCommand()){find.Transaction=tx;find.CommandText="select id from studio_workday_work_sessions where lower(user_name)=lower($1) and ended_at_utc is null order by started_at_utc desc limit 1";find.Parameters.AddWithValue(userName);object? v=find.ExecuteScalar();if(v is Guid g)sessionId=g;}
            if(sessionId is null) message="No active work session.";
            else
            {
                using NpgsqlCommand upd=c.CreateCommand();upd.Transaction=tx;upd.CommandText="update studio_workday_work_sessions set ended_at_utc=$2,ended_offline=$3,updated_at_utc=now() where id=$1";upd.Parameters.AddWithValue(sessionId.Value);upd.Parameters.AddWithValue(request.OccurredAtUtc);upd.Parameters.AddWithValue(request.Text=="offline");upd.ExecuteNonQuery();
                SaveLocation(c,tx,userName,sessionId,request,null);
            }
        }
        else if(request.Kind is WorkDayOperationKind.LocationObservation)
        {
            Guid? sessionId=null; using(NpgsqlCommand find=c.CreateCommand()){find.Transaction=tx;find.CommandText="select id from studio_workday_work_sessions where lower(user_name)=lower($1) and ended_at_utc is null limit 1";find.Parameters.AddWithValue(userName);object? v=find.ExecuteScalar();if(v is Guid g)sessionId=g;}
            SaveLocation(c,tx,userName,sessionId,request,request.AssignmentId);
        }
        else if(request.AssignmentId is Guid assignmentId)
        {
            string? next=request.Kind switch { WorkDayOperationKind.AcceptAssignment=>"Accepted",WorkDayOperationKind.Arrive=>"Arrived",WorkDayOperationKind.StartSession=>"InProgress",WorkDayOperationKind.CompleteSession=>"Completed",_=>null};
            if(next is not null)
            {
                long current=0; using(NpgsqlCommand q=c.CreateCommand()){q.Transaction=tx;q.CommandText="select version from studio_workday_assignment_state where assignment_id=$1";q.Parameters.AddWithValue(assignmentId);object? v=q.ExecuteScalar();if(v is long l)current=l;}
                if(request.ExpectedVersion is long expected && current>0 && expected!=current){tx.Rollback();return new(request.OperationId,WorkDayOperationState.Conflict,now,$"Version conflict. Client {expected}; server {current}.",current);}
                version=current+1; using NpgsqlCommand up=c.CreateCommand();up.Transaction=tx;up.CommandText="insert into studio_workday_assignment_state(assignment_id,status,version,changed_by,changed_at_utc,last_operation_id) values($1,$2,$3,$4,$5,$6) on conflict(assignment_id) do update set status=excluded.status,version=excluded.version,changed_by=excluded.changed_by,changed_at_utc=excluded.changed_at_utc,last_operation_id=excluded.last_operation_id";up.Parameters.AddWithValue(assignmentId);up.Parameters.AddWithValue(next);up.Parameters.AddWithValue(version.Value);up.Parameters.AddWithValue(userName);up.Parameters.AddWithValue(request.OccurredAtUtc);up.Parameters.AddWithValue(request.OperationId);up.ExecuteNonQuery();
            }
            if(request.Kind is WorkDayOperationKind.AddNote or WorkDayOperationKind.RecordException)
            {
                if(string.IsNullOrWhiteSpace(request.Text)) throw new InvalidOperationException("Text is required.");
                using NpgsqlCommand note=c.CreateCommand();note.Transaction=tx;note.CommandText="insert into studio_workday_notes(id,operation_id,user_name,assignment_id,note_type,category,body,occurred_at_utc) values($1,$2,$3,$4,$5,$6,$7,$8)";note.Parameters.AddWithValue(Guid.NewGuid());note.Parameters.AddWithValue(request.OperationId);note.Parameters.AddWithValue(userName);note.Parameters.AddWithValue(assignmentId);note.Parameters.AddWithValue(request.Kind==WorkDayOperationKind.AddNote?"InternalNote":"Exception");note.Parameters.AddWithValue((object?)request.Category??DBNull.Value);note.Parameters.AddWithValue(request.Text);note.Parameters.AddWithValue(request.OccurredAtUtc);note.ExecuteNonQuery();
            }
            SaveLocation(c,tx,userName,GetActiveSessionId(c,tx,userName),request,assignmentId);
        }

        using(NpgsqlCommand op=c.CreateCommand()){op.Transaction=tx;op.CommandText="insert into studio_workday_operations(operation_id,user_name,kind,assignment_id,occurred_at_utc,device_id,text_value,category,expected_version,state,result_message,server_version,received_at_utc,acknowledged_at_utc) values($1,$2,$3,$4,$5,$6,$7,$8,$9,'Acknowledged',$10,$11,now(),now())";op.Parameters.AddWithValue(request.OperationId);op.Parameters.AddWithValue(userName);op.Parameters.AddWithValue(request.Kind.ToString());op.Parameters.AddWithValue((object?)request.AssignmentId??DBNull.Value);op.Parameters.AddWithValue(request.OccurredAtUtc);op.Parameters.AddWithValue(request.DeviceId);op.Parameters.AddWithValue((object?)request.Text??DBNull.Value);op.Parameters.AddWithValue((object?)request.Category??DBNull.Value);op.Parameters.AddWithValue((object?)request.ExpectedVersion??DBNull.Value);op.Parameters.AddWithValue(message);op.Parameters.AddWithValue((object?)version??DBNull.Value);op.ExecuteNonQuery();}
        tx.Commit(); return new(request.OperationId,WorkDayOperationState.Acknowledged,now,message,version);
    }

    private static Guid? GetActiveSessionId(NpgsqlConnection c,NpgsqlTransaction tx,string user){using NpgsqlCommand q=c.CreateCommand();q.Transaction=tx;q.CommandText="select id from studio_workday_work_sessions where lower(user_name)=lower($1) and ended_at_utc is null limit 1";q.Parameters.AddWithValue(user);return q.ExecuteScalar() is Guid g?g:null;}
    private static void SaveLocation(NpgsqlConnection c,NpgsqlTransaction tx,string user,Guid? session,WorkDayOperationRequest request,Guid? assignment){if(request.Location is not { } l)return;using NpgsqlCommand q=c.CreateCommand();q.Transaction=tx;q.CommandText="insert into studio_workday_location_observations(id,user_name,work_session_id,assignment_id,operation_id,latitude,longitude,accuracy_metres,observed_at_utc,source,mock_location,received_at_utc) values($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,now()) on conflict(id) do nothing";q.Parameters.AddWithValue(l.ObservationId);q.Parameters.AddWithValue(user);q.Parameters.AddWithValue((object?)session??DBNull.Value);q.Parameters.AddWithValue((object?)assignment??DBNull.Value);q.Parameters.AddWithValue(request.OperationId);q.Parameters.AddWithValue(l.Latitude);q.Parameters.AddWithValue(l.Longitude);q.Parameters.AddWithValue((object?)l.AccuracyMetres??DBNull.Value);q.Parameters.AddWithValue(l.ObservedAtUtc);q.Parameters.AddWithValue((object?)l.Source??DBNull.Value);q.Parameters.AddWithValue((object?)l.MockLocation??DBNull.Value);q.ExecuteNonQuery();}
}
