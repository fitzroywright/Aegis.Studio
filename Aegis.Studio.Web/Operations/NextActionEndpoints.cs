using System.Security.Claims;
using System.Text.Json;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Production;
using Aegis.Studio.Web.Workflow;
using Npgsql;

namespace Aegis.Studio.Web.Operations;

public sealed record NextActionRow(string Kind, Guid EntityId, Guid? JobId, Guid ConversationId, Guid WorkspaceId,
    Guid? ClientId, string EntityTitle, NextActionChoice Action, string? AssignedTo, DateTimeOffset? DueAtUtc,
    string? WaitingReason, long Version, bool CanManage, bool CanAttach = false);
public sealed record SaveNextAction(string ActionKey, string? AssignedTo, DateTimeOffset? DueAtUtc, string? WaitingReason, long ExpectedVersion);

public sealed class NextActionStore(StudioDatabase db, JobAccessService jobs, WorkspaceAccessService workspaces)
{
    public static void EnsureSchema(StudioDatabase db)
    {
        using var c = db.OpenConnection();
        using var q = new NpgsqlCommand("""
            create table if not exists studio_next_action_details(
              kind text not null check(kind in ('job','enquiry')),entity_id uuid not null,action_key text not null,
              assigned_to text null,due_at_utc timestamptz null,waiting_reason text null,version bigint not null,
              updated_by text not null,updated_at_utc timestamptz not null default now(),primary key(kind,entity_id,action_key));
            create table if not exists studio_next_action_audit(
              id uuid primary key,kind text not null,entity_id uuid not null,action_key text not null,
              actor text not null,details jsonb not null,created_at_utc timestamptz not null default now());
            create index if not exists ix_next_action_audit_entity on studio_next_action_audit(kind,entity_id,created_at_utc);
            """, c);
        q.ExecuteNonQuery();
    }

    public NextActionRow[] List(ClaimsPrincipal user, string? kind = null, Guid? id = null)
    {
        var result = new List<NextActionRow>();
        var permissions = jobs.GetPermissionMap(user);
        Guid[] ids = permissions.Where(p => p.Value.ViewJob && (kind is null or "job") && (!id.HasValue || id == p.Key)).Select(p => p.Key).ToArray();
        using var c = db.OpenConnection();
        // Load summary facts in one query. Only production tasks need graph evaluation.
        using var q = new NpgsqlCommand("""
            select j.id,j.workspace_id,j.conversation_id,j.client_id,j.title,j.status,j.job_lead,a.status,j.blocked_reason,j.correction_kind,
              exists(select 1 from studio_frontdesk_requests r where r.job_id=j.id),
              coalesce((select greatest(0,(r.payload->>'requiredDeposit')::numeric-coalesce((select sum(d.amount) from studio_frontdesk_deposits d where d.request_id=r.id),0)) from studio_frontdesk_requests r where r.job_id=j.id order by r.created_at_utc desc limit 1),0),
              (select count(*)::int from studio_job_subjects s where s.job_id=j.id and not s.is_system),
              (select count(*)::int from studio_media_assets m where m.job_id=j.id and m.deleted_at_utc is null and not exists(select 1 from studio_media_asset_subjects x join studio_job_subjects s on s.id=x.subject_id where x.asset_id=m.id and not s.is_system))
            from studio_jobs j left join studio_job_owner_assignments a on a.id=j.owner_assignment_id where j.id=any($1) and j.status not in ('Completed','Closed','Cancelled','Archived')
            """, c);
        q.Parameters.AddWithValue(ids);
        var facts = new List<(Guid Id, Guid Workspace, Guid Conversation, Guid? Client, string Title, NextActionFacts Facts)>();
        using (var r = q.ExecuteReader()) while (r.Read()) facts.Add((r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.IsDBNull(3) ? null : r.GetGuid(3), r.GetString(4),
            new(r.GetString(5), !r.IsDBNull(3), Text(r,6), Text(r,7), r.GetDecimal(11), r.GetBoolean(10), r.GetInt32(12), r.GetInt32(13), Text(r,8), Text(r,9), [])));
        var graphs = new JobGraphStore(db);
        foreach (var f in facts)
        {
            var state = f.Facts;
            if (state.Stage == "Active Production") state = state with { Tasks = graphs.Get(f.Id)?.Tasks ?? [] };
            var choice = NextActionRules.Job(state);
            if (choice is null) continue;
            DateTimeOffset? due = null;
            if (choice.TaskId is Guid task && task != Guid.Empty)
            {
                using var tq = new NpgsqlCommand("select due_at_utc from studio_job_activities where id=$1 and job_id=$2", c);
                tq.Parameters.AddWithValue(task); tq.Parameters.AddWithValue(f.Id);
                using var tr = tq.ExecuteReader(); if (tr.Read() && !tr.IsDBNull(0)) due = tr.GetFieldValue<DateTimeOffset>(0);
            }
            result.Add(new("job", f.Id, f.Id, f.Conversation, f.Workspace, f.Client, f.Title, choice,
                choice.AssignedTo ?? state.Owner, due, null, 0, permissions[f.Id].EditJob || permissions[f.Id].ManageProduction));
        }
        if (kind is null or "enquiry")
        {
            Guid[] allowed = workspaces.GetAccessibleWorkspaceIds(user).ToArray();
            using var eq = new NpgsqlCommand("""
                select v.id,v.workspace_id,v.client_id,v.subject,
                  not exists(select 1 from studio_messages reply where reply.conversation_id=v.id and reply.visibility='External'
                    and reply.external_message_id is null and reply.author_user_name is not null
                    and lower(reply.author_user_name)<>lower(v.contact_address)
                    and reply.created_at_utc >= coalesce((select max(m.created_at_utc) from studio_messages m where m.conversation_id=v.id and m.visibility='External' and (m.external_message_id is not null or m.author_user_name is null or lower(m.author_user_name)=lower(v.contact_address))),v.created_at_utc)),
                  (select case when count(*)=1 then min(quote.status) else null end from studio_client_quotes quote
                    where quote.client_id=v.client_id and quote.payload->>'workspaceId'=v.workspace_id::text
                    and quote.payload->>'title'=v.subject and quote.status<>'Converted')
                from studio_conversations v where v.job_id is null and not v.is_closed and v.workspace_id=any($1) and ($2::uuid is null or v.id=$2)
                """, c);
            eq.Parameters.AddWithValue(allowed); eq.Parameters.AddWithValue((object?)id ?? DBNull.Value);
            var enquiries = new List<(Guid Id, Guid Workspace, Guid? Client, string Title, bool NeedsReply, string? Quote)>();
            using (var r = eq.ExecuteReader()) while(r.Read()) enquiries.Add((r.GetGuid(0),r.GetGuid(1),r.IsDBNull(2)?null:r.GetGuid(2),r.GetString(3),r.GetBoolean(4),Text(r,5)));
            foreach (var e in enquiries)
            {
                if (!workspaces.HasPermission(user,e.Workspace,"View")) continue;
                var choice = NextActionRules.Enquiry(false,e.Client.HasValue,e.NeedsReply,e.Quote)!;
                result.Add(new("enquiry",e.Id,null,e.Id,e.Workspace,e.Client,e.Title,choice,null,null,null,0,workspaces.HasPermission(user,e.Workspace,"ReplyRequester"),workspaces.HasPermission(user,e.Workspace,"AddAttachments")));
            }
        }
        if (result.Count == 0) return [];
        var overrides = new Dictionary<(string,Guid,string),(string? Owner,DateTimeOffset? Due,string? Waiting,long Version)>();
        using var mq = new NpgsqlCommand("select kind,entity_id,action_key,assigned_to,due_at_utc,waiting_reason,version from studio_next_action_details where entity_id=any($1)",c);
        mq.Parameters.AddWithValue(result.Select(r=>r.EntityId).Distinct().ToArray());
        using(var r=mq.ExecuteReader()) while(r.Read()) overrides[(r.GetString(0),r.GetGuid(1),r.GetString(2))]=(Text(r,3),r.IsDBNull(4)?null:r.GetFieldValue<DateTimeOffset>(4),Text(r,5),r.GetInt64(6));
        return result.Select(row=>overrides.TryGetValue((row.Kind,row.EntityId,row.Action.Key),out var saved)
            ? row with{AssignedTo=saved.Owner,DueAtUtc=saved.Due,WaitingReason=saved.Waiting,Version=saved.Version}:row).ToArray();
    }

    public NextActionRow Save(ClaimsPrincipal user,string kind,Guid id,SaveNextAction request)
    {
        if(kind is not ("job" or "enquiry") || request.ExpectedVersion<0 || string.IsNullOrWhiteSpace(request.ActionKey) || request.ActionKey.Length>300
            || request.AssignedTo?.Length>200 || request.WaitingReason?.Length>2000) throw new ArgumentException("Supply a valid next action and details.");
        var current=List(user,kind,id).SingleOrDefault()??throw new KeyNotFoundException();
        if(!current.CanManage) throw new UnauthorizedAccessException();
        if(current.Action.Key!=request.ActionKey || current.Version!=request.ExpectedVersion) throw new WorkflowConflictException();
        using var c=db.OpenConnection();using var tx=c.BeginTransaction();
        // Serialize changes with workflow edits; assignments apply only to this action.
        using(var guard=new NpgsqlCommand(kind=="job"?"select id from studio_jobs where id=$1 for update":"select id from studio_conversations where id=$1 and job_id is null and not is_closed for update",c,tx))
        {guard.Parameters.AddWithValue(id);if(guard.ExecuteScalar() is null)throw new WorkflowConflictException();}
        current=List(user,kind,id).SingleOrDefault()??throw new WorkflowConflictException();
        if(!current.CanManage)throw new UnauthorizedAccessException();
        if(current.Action.Key!=request.ActionKey||current.Version!=request.ExpectedVersion)throw new WorkflowConflictException();
        if(!string.IsNullOrWhiteSpace(request.AssignedTo))
        {
            using var person=new NpgsqlCommand("select exists(select 1 from studio_employees where lower(user_name)=lower($1) and is_active and is_assignable)",c,tx);
            person.Parameters.AddWithValue(request.AssignedTo.Trim());if(!(bool)person.ExecuteScalar()!)throw new ArgumentException("Choose an active, assignable Studio employee.");
        }
        using var q=new NpgsqlCommand("""
            insert into studio_next_action_details(kind,entity_id,action_key,assigned_to,due_at_utc,waiting_reason,version,updated_by)
            values($1,$2,$3,$4,$5,$6,1,$7) on conflict(kind,entity_id,action_key) do update set
              assigned_to=excluded.assigned_to,due_at_utc=excluded.due_at_utc,waiting_reason=excluded.waiting_reason,
              version=studio_next_action_details.version+1,updated_by=excluded.updated_by,updated_at_utc=now()
            where studio_next_action_details.version=$8 returning version
            """,c,tx);
        Add(q,kind,id,request.ActionKey,Clean(request.AssignedTo),request.DueAtUtc?.ToUniversalTime(),Clean(request.WaitingReason),WorkflowStore.Name(user),request.ExpectedVersion);
        if(q.ExecuteScalar() is null)throw new WorkflowConflictException();
        using var audit=new NpgsqlCommand("insert into studio_next_action_audit(id,kind,entity_id,action_key,actor,details) values($1,$2,$3,$4,$5,$6::jsonb)",c,tx);
        Add(audit,Guid.NewGuid(),kind,id,request.ActionKey,WorkflowStore.Name(user),JsonSerializer.Serialize(new{before=current,after=request}));audit.ExecuteNonQuery();
        tx.Commit();return List(user,kind,id).SingleOrDefault()??throw new WorkflowConflictException();
    }
    private static string? Text(NpgsqlDataReader r,int i)=>r.IsDBNull(i)?null:r.GetString(i);
    private static string? Clean(string? value)=>string.IsNullOrWhiteSpace(value)?null:value.Trim();
    private static void Add(NpgsqlCommand q,params object?[] values){foreach(var value in values)q.Parameters.AddWithValue(value??DBNull.Value);}
}

public static class NextActionEndpoints
{
    public static void MapStudioNextActions(this WebApplication app)
    {
        NextActionStore.EnsureSchema(app.Services.GetRequiredService<StudioDatabase>());
        app.MapGet("/api/work/next-actions",(string? kind,Guid? id,ClaimsPrincipal user,StudioDatabase db,JobAccessService jobs,WorkspaceAccessService workspaces)=>WorkflowEndpoints.Run(()=>
            kind is not (null or "job" or "enquiry") ? Results.BadRequest(new{error="Choose job or enquiry."}) : Results.Ok(new NextActionStore(db,jobs,workspaces).List(user,kind,id)))).RequireAuthorization();
        app.MapPut("/api/work/next-actions/{kind}/{id:guid}",(string kind,Guid id,SaveNextAction request,ClaimsPrincipal user,StudioDatabase db,JobAccessService jobs,WorkspaceAccessService workspaces)=>WorkflowEndpoints.Run(()=>
            Results.Ok(new NextActionStore(db,jobs,workspaces).Save(user,kind,id,request)))).RequireAuthorization();
    }
}
