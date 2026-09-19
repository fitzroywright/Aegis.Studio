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
          assignment_id uuid null, operation_id uuid null,
          latitude double precision not null, longitude double precision not null, accuracy_metres double precision null,
          observed_at_utc timestamptz not null, source text null, mock_location boolean null, received_at_utc timestamptz not null);
        alter table studio_workday_location_observations drop constraint if exists studio_workday_location_observations_operation_id_fkey;
        create index if not exists ix_workday_location_session on studio_workday_location_observations(work_session_id,observed_at_utc);

        create table if not exists studio_workday_notes(
          id uuid primary key, operation_id uuid not null unique references studio_workday_operations(operation_id),
          user_name text not null, assignment_id uuid not null, note_type text not null,
          category text null, body text not null, occurred_at_utc timestamptz not null);

        create table if not exists studio_workday_assignment_state(
          assignment_id uuid primary key, status text not null, version bigint not null default 1,
          changed_by text not null, changed_at_utc timestamptz not null, last_operation_id uuid not null);

        create table if not exists studio_workday_device_sessions(
          id uuid primary key, user_name text not null, device_id text not null,
          token_hash text not null unique, created_at_utc timestamptz not null,
          expires_at_utc timestamptz not null, revoked_at_utc timestamptz null,
          last_seen_at_utc timestamptz null);
        create index if not exists ix_workday_device_sessions_user on studio_workday_device_sessions(lower(user_name),expires_at_utc);

        create table if not exists studio_workday_client_observations(
          device_id text primary key, user_name text not null, observed_at_utc timestamptz not null,
          connectivity text not null, synchronization_state text not null, pending_operations integer not null,
          oldest_pending_at_utc timestamptz null, pending_uploads integer not null, failed_uploads integer not null,
          work_session_active boolean not null, location_permission_state text not null, received_at_utc timestamptz not null);

        create table if not exists studio_workday_location_access_audit(
          id uuid primary key, viewer_user_name text not null, target_user_name text not null,
          work_session_id uuid null, action text not null, viewed_at_utc timestamptz not null);
        create index if not exists ix_workday_location_access_audit_time on studio_workday_location_access_audit(viewed_at_utc desc);

        create table if not exists studio_workday_session_corrections(
          id uuid primary key, work_session_id uuid not null references studio_workday_work_sessions(id),
          user_name text not null, original_started_at_utc timestamptz not null, original_ended_at_utc timestamptz null,
          corrected_started_at_utc timestamptz null, corrected_ended_at_utc timestamptz null,
          reason text not null, corrected_by text not null, corrected_at_utc timestamptz not null);
        create index if not exists ix_workday_session_corrections_session on studio_workday_session_corrections(work_session_id,corrected_at_utc);
        """;
        cmd.ExecuteNonQuery();
    }

    public (Guid SessionId,string Token,DateTimeOffset ExpiresAtUtc) IssueDeviceSession(string userName,string deviceId,TimeSpan lifetime)
    {
        if(string.IsNullOrWhiteSpace(userName)||string.IsNullOrWhiteSpace(deviceId)) throw new InvalidOperationException("User and device identity are required.");
        byte[] tokenBytes=System.Security.Cryptography.RandomNumberGenerator.GetBytes(48);
        string token=Convert.ToBase64String(tokenBytes);
        string hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        Guid id=Guid.NewGuid(); DateTimeOffset expires=DateTimeOffset.UtcNow.Add(lifetime);
        using NpgsqlConnection c=database.OpenConnection(); using NpgsqlCommand q=c.CreateCommand();
        q.CommandText="insert into studio_workday_device_sessions(id,user_name,device_id,token_hash,created_at_utc,expires_at_utc) values($1,$2,$3,$4,now(),$5)";
        q.Parameters.AddWithValue(id);q.Parameters.AddWithValue(userName);q.Parameters.AddWithValue(deviceId);q.Parameters.AddWithValue(hash);q.Parameters.AddWithValue(expires);q.ExecuteNonQuery();
        return(id,token,expires);
    }

    public (string UserName,string DeviceId)? ValidateDeviceToken(string token)
    {
        if(string.IsNullOrWhiteSpace(token)) return null;
        string hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        using NpgsqlConnection c=database.OpenConnection(); using NpgsqlCommand q=c.CreateCommand();
        q.CommandText="select user_name,device_id,id from studio_workday_device_sessions where token_hash=$1 and revoked_at_utc is null and expires_at_utc>now()";
        q.Parameters.AddWithValue(hash); using NpgsqlDataReader r=q.ExecuteReader(); if(!r.Read()) return null;
        string user=r.GetString(0),device=r.GetString(1);Guid id=r.GetGuid(2);r.Close();
        using NpgsqlCommand touch=c.CreateCommand();touch.CommandText="update studio_workday_device_sessions set last_seen_at_utc=now() where id=$1";touch.Parameters.AddWithValue(id);touch.ExecuteNonQuery();
        return(user,device);
    }

    public void RevokeDeviceSession(string userName,string deviceId)
    {
        using NpgsqlConnection c=database.OpenConnection(); using NpgsqlCommand q=c.CreateCommand();
        q.CommandText="update studio_workday_device_sessions set revoked_at_utc=now() where lower(user_name)=lower($1) and device_id=$2 and revoked_at_utc is null";
        q.Parameters.AddWithValue(userName);q.Parameters.AddWithValue(deviceId);q.ExecuteNonQuery();
    }

    public (string Status, long Version)? GetAssignmentState(Guid assignmentId)
    {
        using NpgsqlConnection c=database.OpenConnection(); using NpgsqlCommand cmd=c.CreateCommand();
        cmd.CommandText="select status,version from studio_workday_assignment_state where assignment_id=$1";
        cmd.Parameters.AddWithValue(assignmentId);
        using NpgsqlDataReader r=cmd.ExecuteReader();
        return r.Read() ? (r.GetString(0), r.GetInt64(1)) : null;
    }

    public IReadOnlyList<(Guid OperationId,string Kind,Guid? AssignmentId,DateTimeOffset OccurredAtUtc,string State,string? Message)> GetRecentOperations(string userName,int count=100)
    {
        using NpgsqlConnection c=database.OpenConnection(); using NpgsqlCommand cmd=c.CreateCommand();
        cmd.CommandText="select operation_id,kind,assignment_id,occurred_at_utc,state,result_message from studio_workday_operations where lower(user_name)=lower($1) order by occurred_at_utc desc limit $2";
        cmd.Parameters.AddWithValue(userName); cmd.Parameters.AddWithValue(Math.Clamp(count,1,500));
        using NpgsqlDataReader r=cmd.ExecuteReader();
        var values=new List<(Guid,string,Guid?,DateTimeOffset,string,string?)>();
        while(r.Read()) values.Add((r.GetGuid(0),r.GetString(1),r.IsDBNull(2)?null:r.GetGuid(2),r.GetFieldValue<DateTimeOffset>(3),r.GetString(4),r.IsDBNull(5)?null:r.GetString(5)));
        return values;
    }

    public IReadOnlyList<WorkDayTimeSummary> GetTimeSummaries(
        IReadOnlyCollection<string> userNames,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        WorkDayTimePolicy policy)
    {
        if(userNames.Count==0) return [];
        string[] users=userNames.Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if(users.Length==0) return [];

        using NpgsqlConnection c=database.OpenConnection(); using NpgsqlCommand q=c.CreateCommand();
        q.CommandText="""
          select user_name,started_at_utc,coalesce(ended_at_utc,least(now(),$3))
          from studio_workday_work_sessions
          where lower(user_name)=any($1)
            and started_at_utc<$3
            and coalesce(ended_at_utc,now())>$2
          order by lower(user_name),started_at_utc
        """;
        q.Parameters.AddWithValue(users.Select(x=>x.ToLowerInvariant()).ToArray());
        q.Parameters.AddWithValue(fromUtc);
        q.Parameters.AddWithValue(toUtc);
        using NpgsqlDataReader r=q.ExecuteReader();
        List<WorkDayTimeInterval> intervals=[];
        while(r.Read())
        {
            DateTimeOffset start=r.GetFieldValue<DateTimeOffset>(1);
            DateTimeOffset end=r.GetFieldValue<DateTimeOffset>(2);
            if(end>start) intervals.Add(new(r.GetString(0),start,end));
        }

        IReadOnlyList<WorkDayTimeSummary> classified=WorkDayTimeSemantics.Classify(intervals,fromUtc,toUtc,policy);
        Dictionary<string,WorkDayTimeSummary> byUser=classified.ToDictionary(x=>x.UserName,StringComparer.OrdinalIgnoreCase);
        return users.Select(user=>byUser.TryGetValue(user,out WorkDayTimeSummary? value)
            ? value
            : new WorkDayTimeSummary(user,fromUtc,toUtc,0,0,0,0,0,[])).ToArray();
    }

    public IReadOnlyList<WorkDayWorkSession> GetWorkSessions(string? userName,int count=100)
    {
        using NpgsqlConnection c=database.OpenConnection(); using NpgsqlCommand q=c.CreateCommand();
        q.CommandText="""
          select id,user_name,started_at_utc,ended_at_utc,device_id,started_offline,ended_offline,source,updated_at_utc
          from studio_workday_work_sessions
          where ($1::text is null or lower(user_name)=lower($1))
          order by started_at_utc desc limit $2
        """;
        q.Parameters.AddWithValue((object?)userName??DBNull.Value);q.Parameters.AddWithValue(Math.Clamp(count,1,500));
        using NpgsqlDataReader r=q.ExecuteReader();var values=new List<WorkDayWorkSession>();
        while(r.Read()) values.Add(new(r.GetGuid(0),r.GetString(1),r.GetFieldValue<DateTimeOffset>(2),r.IsDBNull(3)?null:r.GetFieldValue<DateTimeOffset>(3),r.GetString(4),r.GetBoolean(5),r.IsDBNull(6)?null:r.GetBoolean(6),null,null,r.GetString(7),r.GetFieldValue<DateTimeOffset>(8)));
        return values;
    }

    public void AuditLocationAccess(string viewerUserName,string targetUserName,Guid? workSessionId,string action)
    {
        using NpgsqlConnection c=database.OpenConnection(); using NpgsqlCommand q=c.CreateCommand();
        q.CommandText="insert into studio_workday_location_access_audit(id,viewer_user_name,target_user_name,work_session_id,action,viewed_at_utc) values($1,$2,$3,$4,$5,now())";
        q.Parameters.AddWithValue(Guid.NewGuid());q.Parameters.AddWithValue(viewerUserName);q.Parameters.AddWithValue(targetUserName);
        q.Parameters.AddWithValue((object?)workSessionId??DBNull.Value);q.Parameters.AddWithValue(action);q.ExecuteNonQuery();
    }

    public int ApplyLocationRetention(TimeSpan retention)
    {
        if(retention<TimeSpan.FromDays(1)) retention=TimeSpan.FromDays(1);
        DateTimeOffset cutoff=DateTimeOffset.UtcNow-retention;
        using NpgsqlConnection c=database.OpenConnection(); using NpgsqlCommand q=c.CreateCommand();
        q.CommandText="delete from studio_workday_location_observations where observed_at_utc<$1";
        q.Parameters.AddWithValue(cutoff);
        return q.ExecuteNonQuery();
    }

    public WorkDaySessionCorrection CorrectWorkSession(Guid workSessionId,WorkDaySessionCorrectionRequest request,string correctedBy)
    {
        if(string.IsNullOrWhiteSpace(request.Reason)) throw new InvalidOperationException("Correction reason is required.");
        using NpgsqlConnection c=database.OpenConnection(); using NpgsqlTransaction tx=c.BeginTransaction();
        string? userName=null;DateTimeOffset originalStart=default;DateTimeOffset? originalEnd=null;
        using(NpgsqlCommand find=c.CreateCommand())
        {
            find.Transaction=tx;find.CommandText="select user_name,started_at_utc,ended_at_utc from studio_workday_work_sessions where id=$1 for update";
            find.Parameters.AddWithValue(workSessionId);using NpgsqlDataReader r=find.ExecuteReader();
            if(!r.Read()) throw new KeyNotFoundException("Work session was not found.");
            userName=r.GetString(0);originalStart=r.GetFieldValue<DateTimeOffset>(1);originalEnd=r.IsDBNull(2)?null:r.GetFieldValue<DateTimeOffset>(2);
        }
        DateTimeOffset newStart=request.CorrectedStartedAtUtc??originalStart;
        DateTimeOffset? newEnd=request.CorrectedEndedAtUtc??originalEnd;
        if(newEnd.HasValue&&newEnd.Value<newStart) throw new InvalidOperationException("Corrected sign-out cannot be before sign-in.");
        Guid correctionId=Guid.NewGuid();DateTimeOffset now=DateTimeOffset.UtcNow;
        using(NpgsqlCommand audit=c.CreateCommand())
        {
            audit.Transaction=tx;audit.CommandText="insert into studio_workday_session_corrections(id,work_session_id,user_name,original_started_at_utc,original_ended_at_utc,corrected_started_at_utc,corrected_ended_at_utc,reason,corrected_by,corrected_at_utc) values($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)";
            audit.Parameters.AddWithValue(correctionId);audit.Parameters.AddWithValue(workSessionId);audit.Parameters.AddWithValue(userName!);audit.Parameters.AddWithValue(originalStart);
            audit.Parameters.AddWithValue((object?)originalEnd??DBNull.Value);audit.Parameters.AddWithValue((object?)request.CorrectedStartedAtUtc??DBNull.Value);
            audit.Parameters.AddWithValue((object?)request.CorrectedEndedAtUtc??DBNull.Value);audit.Parameters.AddWithValue(request.Reason.Trim());audit.Parameters.AddWithValue(correctedBy);audit.Parameters.AddWithValue(now);audit.ExecuteNonQuery();
        }
        using(NpgsqlCommand update=c.CreateCommand())
        {
            update.Transaction=tx;update.CommandText="update studio_workday_work_sessions set started_at_utc=$2,ended_at_utc=$3,updated_at_utc=now() where id=$1";
            update.Parameters.AddWithValue(workSessionId);update.Parameters.AddWithValue(newStart);update.Parameters.AddWithValue((object?)newEnd??DBNull.Value);update.ExecuteNonQuery();
        }
        tx.Commit();
        return new(correctionId,workSessionId,userName!,originalStart,originalEnd,request.CorrectedStartedAtUtc,request.CorrectedEndedAtUtc,request.Reason.Trim(),correctedBy,now);
    }

    public IReadOnlyList<WorkDayLocationEvidence> GetLocationTimeline(string userName, Guid workSessionId)
    {
        using NpgsqlConnection c=database.OpenConnection(); using NpgsqlCommand cmd=c.CreateCommand();
        cmd.CommandText="select id,latitude,longitude,accuracy_metres,observed_at_utc,source,mock_location from studio_workday_location_observations where lower(user_name)=lower($1) and work_session_id=$2 order by observed_at_utc";
        cmd.Parameters.AddWithValue(userName); cmd.Parameters.AddWithValue(workSessionId);
        using NpgsqlDataReader r=cmd.ExecuteReader(); var values=new List<WorkDayLocationEvidence>();
        while(r.Read()) values.Add(new(r.GetGuid(0),r.GetDouble(1),r.GetDouble(2),r.IsDBNull(3)?null:r.GetDouble(3),r.GetFieldValue<DateTimeOffset>(4),r.IsDBNull(5)?null:r.GetString(5),r.IsDBNull(6)?null:r.GetBoolean(6)));
        return values;
    }

    public void ObserveClient(string userName, WorkDayClientObservation observation)
    {
        if(string.IsNullOrWhiteSpace(observation.DeviceId)) throw new InvalidOperationException("DeviceId is required.");
        using NpgsqlConnection c=database.OpenConnection(); using NpgsqlCommand q=c.CreateCommand();
        q.CommandText="""
          insert into studio_workday_client_observations(device_id,user_name,observed_at_utc,connectivity,synchronization_state,pending_operations,oldest_pending_at_utc,pending_uploads,failed_uploads,work_session_active,location_permission_state,received_at_utc)
          values($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,now())
          on conflict(device_id) do update set user_name=excluded.user_name,observed_at_utc=excluded.observed_at_utc,connectivity=excluded.connectivity,
          synchronization_state=excluded.synchronization_state,pending_operations=excluded.pending_operations,oldest_pending_at_utc=excluded.oldest_pending_at_utc,
          pending_uploads=excluded.pending_uploads,failed_uploads=excluded.failed_uploads,work_session_active=excluded.work_session_active,
          location_permission_state=excluded.location_permission_state,received_at_utc=now()
        """;
        q.Parameters.AddWithValue(observation.DeviceId);q.Parameters.AddWithValue(userName);q.Parameters.AddWithValue(observation.ObservedAtUtc);
        q.Parameters.AddWithValue(observation.Connectivity);q.Parameters.AddWithValue(observation.SynchronizationState);q.Parameters.AddWithValue(Math.Max(0,observation.PendingOperations));
        q.Parameters.AddWithValue((object?)observation.OldestPendingAtUtc??DBNull.Value);q.Parameters.AddWithValue(Math.Max(0,observation.PendingUploads));
        q.Parameters.AddWithValue(Math.Max(0,observation.FailedUploads));q.Parameters.AddWithValue(observation.WorkSessionActive);
        q.Parameters.AddWithValue(string.IsNullOrWhiteSpace(observation.LocationPermissionState)?"Unknown":observation.LocationPermissionState);
        q.ExecuteNonQuery();
    }

    public IReadOnlyList<WorkDayClientStatus> GetClientStatuses(IReadOnlyCollection<string> userNames)
    {
        if(userNames.Count==0) return [];
        string[] users=userNames.Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if(users.Length==0) return [];
        using NpgsqlConnection c=database.OpenConnection(); using NpgsqlCommand q=c.CreateCommand();
        q.CommandText="""
          select device_id,user_name,observed_at_utc,connectivity,synchronization_state,pending_operations,
                 oldest_pending_at_utc,pending_uploads,failed_uploads,work_session_active,location_permission_state
          from studio_workday_client_observations
          where lower(user_name)=any($1)
          order by observed_at_utc desc
        """;
        q.Parameters.AddWithValue(users.Select(x=>x.ToLowerInvariant()).ToArray());
        using NpgsqlDataReader r=q.ExecuteReader(); List<WorkDayClientStatus> values=[];
        while(r.Read()) values.Add(new(
            r.GetString(0),r.GetString(1),r.GetFieldValue<DateTimeOffset>(2),r.GetString(3),r.GetString(4),r.GetInt32(5),
            r.IsDBNull(6)?null:r.GetFieldValue<DateTimeOffset>(6),r.GetInt32(7),r.GetInt32(8),r.GetBoolean(9),r.GetString(10)));
        return values;
    }

    public WorkDayFleetSnapshot GetFleetSnapshot(TimeSpan staleAfter)
    {
        DateTimeOffset now=DateTimeOffset.UtcNow; DateTimeOffset cutoff=now-staleAfter;
        using NpgsqlConnection c=database.OpenConnection(); using NpgsqlCommand q=c.CreateCommand();
        q.CommandText="""
          select count(*),
          count(*) filter(where observed_at_utc >= $1 and lower(connectivity)='online'),
          count(*) filter(where observed_at_utc < $1 or lower(connectivity)<>'online'),
          coalesce(sum(pending_operations),0),
          min(oldest_pending_at_utc),
          coalesce(sum(pending_uploads),0),
          coalesce(sum(failed_uploads),0)
          from studio_workday_client_observations
        """;
        q.Parameters.AddWithValue(cutoff); using NpgsqlDataReader r=q.ExecuteReader();r.Read();
        return new WorkDayFleetSnapshot(
            checked((int)r.GetInt64(0)),
            checked((int)r.GetInt64(1)),
            checked((int)r.GetInt64(2)),
            r.GetInt64(3),
            r.IsDBNull(4)?null:r.GetFieldValue<DateTimeOffset>(4),
            r.GetInt64(5),
            r.GetInt64(6),
            now);
    }

    public WorkDayDiagnosticsSnapshot GetDiagnostics(string userName)
    {
        using NpgsqlConnection c=database.OpenConnection();
        long pending=0, failed=0; DateTimeOffset? oldest=null;
        using(NpgsqlCommand q=c.CreateCommand())
        {
            q.CommandText="select count(*) filter(where state='Pending'),count(*) filter(where state in ('Failed','Conflict')),min(occurred_at_utc) filter(where state='Pending') from studio_workday_operations where lower(user_name)=lower($1)";
            q.Parameters.AddWithValue(userName);
            using NpgsqlDataReader r=q.ExecuteReader();
            if(r.Read()){pending=r.GetInt64(0);failed=r.GetInt64(1);oldest=r.IsDBNull(2)?null:r.GetFieldValue<DateTimeOffset>(2);}
        }
        return new WorkDayDiagnosticsSnapshot("Online","ServerAcknowledged",pending,failed,oldest,0,0,GetActiveSession(userName) is not null,DateTimeOffset.UtcNow);
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

        WorkDayOperationResult? replay = null;
        using(NpgsqlCommand existing=c.CreateCommand())
        {
            existing.Transaction=tx; existing.CommandText="select state,result_message,server_version,coalesce(acknowledged_at_utc,received_at_utc) from studio_workday_operations where operation_id=$1";
            existing.Parameters.AddWithValue(request.OperationId);
            using NpgsqlDataReader r=existing.ExecuteReader();
            if(r.Read())
            {
                replay=new WorkDayOperationResult(
                    request.OperationId,
                    Enum.Parse<WorkDayOperationState>(r.GetString(0)),
                    r.GetFieldValue<DateTimeOffset>(3),
                    r.IsDBNull(1)?null:r.GetString(1),
                    r.IsDBNull(2)?null:r.GetInt64(2));
            }
        }
        if(replay is not null)
        {
            tx.Commit();
            return replay;
        }

        DateTimeOffset now=DateTimeOffset.UtcNow;
        long? version=null;
        string message="Acknowledged";

        if(request.Kind is WorkDayOperationKind.WorkSignIn)
        {
            using(NpgsqlCommand userLock=c.CreateCommand())
            {
                userLock.Transaction=tx;
                userLock.CommandText="select pg_advisory_xact_lock(hashtext(lower($1)))";
                userLock.Parameters.AddWithValue(userName);
                userLock.ExecuteNonQuery();
            }
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
            string? next=request.Kind switch
            {
                WorkDayOperationKind.AcceptAssignment=>"Accepted",
                WorkDayOperationKind.Arrive=>"Arrived",
                WorkDayOperationKind.StartSession=>"InProgress",
                WorkDayOperationKind.CompleteSession=>"Completed",
                WorkDayOperationKind.SetExceptionStatus=>WorkDaySemantics.NormalizeExceptionStatus(request.Category),
                _=>null
            };
            if(request.Kind==WorkDayOperationKind.SetExceptionStatus && next is null)
                throw new InvalidOperationException("Exception status must be Cancelled, NoShow, Delayed or UnableToComplete.");
            if(next is not null)
            {
                long currentVersion=0; string? currentStatus=null;
                using(NpgsqlCommand q=c.CreateCommand())
                {
                    q.Transaction=tx;q.CommandText="select status,version from studio_workday_assignment_state where assignment_id=$1";q.Parameters.AddWithValue(assignmentId);
                    using NpgsqlDataReader r=q.ExecuteReader();if(r.Read()){currentStatus=r.GetString(0);currentVersion=r.GetInt64(1);}
                }
                if(request.ExpectedVersion is long expected && currentVersion>0 && expected!=currentVersion)
                {
                    tx.Rollback();return new(request.OperationId,WorkDayOperationState.Conflict,now,$"Version conflict. Client {expected}; server {currentVersion}.",currentVersion);
                }
                if(!WorkDaySemantics.CanTransition(currentStatus,next))
                    throw new InvalidOperationException($"Invalid WorkDay status transition from {WorkDaySemantics.InitialStatus(currentStatus)} to {next}.");
                version=currentVersion+1;
                using NpgsqlCommand up=c.CreateCommand();up.Transaction=tx;
                up.CommandText="insert into studio_workday_assignment_state(assignment_id,status,version,changed_by,changed_at_utc,last_operation_id) values($1,$2,$3,$4,$5,$6) on conflict(assignment_id) do update set status=excluded.status,version=excluded.version,changed_by=excluded.changed_by,changed_at_utc=excluded.changed_at_utc,last_operation_id=excluded.last_operation_id";
                up.Parameters.AddWithValue(assignmentId);up.Parameters.AddWithValue(next);up.Parameters.AddWithValue(version.Value);up.Parameters.AddWithValue(userName);up.Parameters.AddWithValue(request.OccurredAtUtc);up.Parameters.AddWithValue(request.OperationId);up.ExecuteNonQuery();
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
