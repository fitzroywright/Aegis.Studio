using System.Security.Cryptography;
using System.Text;
using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Workflow;

/// <summary>Explicitly gated, database-only Connected Workflow demonstration fixture.</summary>
internal static class ConnectedWorkflowDemo
{
    private static readonly string[] Stages =
    [
        "Pending","Needs Assignment","Needs Photographer Assignment","Awaiting Scheduling",
        "Scheduled","Photography In Progress","Awaiting Upload","Awaiting Production Review",
        "Active Production","Awaiting Approval","Changes Requested","Ready for Delivery",
        "Delivered","Completed","Cancelled"
    ];
    private static readonly string[] Titles =
    [
        "New Kingston wedding enquiry","Portmore family portraits","Corporate headshots",
        "Graduation portraits","Half-Way Tree family session","Kingston commercial shoot",
        "School photography upload","Wedding selects review","Product catalogue retouch",
        "Executive portraits approval","Reshoot and corrections","Approved event gallery",
        "Family gallery delivered","Completed school portraits","Cancelled event booking"
    ];
    private static readonly string[] Clients =
    [
        "Morgan & Reid","Campbell Family","Harbour Business Group","St Andrew Graduates",
        "Brown Family","Blue Mountain Products","Kingston Academy","Williams & Clarke",
        "Island Craft Collective","Kingston Legal Group","Taylor Family","Portmore Events",
        "Anderson Family","North Coast School","Miller Events"
    ];
    private static readonly string[] WorkspaceNames =
    [
        "Portraits and IDs","Weddings and Events","Schools and Corporate","Commercial and Products"
    ];

    private static Guid Id(string key)
    {
        var bytes=MD5.HashData(Encoding.UTF8.GetBytes("studio-connected-demo-v1:"+key));
        return new Guid(bytes);
    }

    internal static object Generate(StudioDatabase db,string actor)
    {
        using var c=db.OpenConnection();
        using var tx=c.BeginTransaction();
        Lock(c,tx);
        var result=GenerateData(c,tx,actor);
        tx.Commit();
        return result;
    }

    private static object GenerateData(NpgsqlConnection c,NpgsqlTransaction tx,string actor)
    {
        Exec(c,tx,"select set_config('studio.demo_seed','true',true)");
        int created=0;
        for(int w=0;w<WorkspaceNames.Length;w++)
        {
            var workspace=Id("workspace:"+w);
            Exec(c,tx,"insert into studio_workspaces(id,name,route_key,is_enabled,sort_order,created_at_utc,updated_at_utc) values($1,$2,$3,true,$4,now(),now()) on conflict(id) do nothing",
                workspace,"Demo | "+WorkspaceNames[w],"connected-demo-"+w,300+w);
            Exec(c,tx,@"insert into studio_workspace_members(workspace_id,user_name,display_name,is_active,access_workspace,can_view,reply_requester,view_attachments,add_attachments,create_job,edit_job,manage_schedule,manage_production,notify_members,edit_workspace,manage_members,admin_workspace,send_as_self,created_at_utc,updated_at_utc)
                values($1,$2,$2,true,true,true,true,true,true,true,true,true,true,true,true,true,true,false,now(),now()) on conflict(workspace_id,user_name) do nothing",workspace,actor);
        }
        for(int i=0;i<Stages.Length;i++)
        {
            var client=Id("client:"+i);
            var conversation=Id("conversation:"+i);
            var job=Id("job:"+i);
            var workspace=Id("workspace:"+(i%WorkspaceNames.Length));
            string email="connected-demo-"+(i+1)+"@example.invalid";
            Exec(c,tx,"insert into studio_clients(id,display_name,email,phone) values($1,$2,$3,$4) on conflict(id) do nothing",client,Clients[i],email,"876-555-"+(1000+i));
            Exec(c,tx,@"insert into studio_conversations(id,workspace_id,subject,contact_name,contact_address,channel,created_at_utc)
                values($1,$2,$3,$4,$5,'Portal',now()-($6*interval '1 day')) on conflict(id) do nothing",
                conversation,workspace,Titles[i],Clients[i],email,i+1);
            string? owner=i<2?null:actor;
            Exec(c,tx,@"insert into studio_jobs(id,workspace_id,conversation_id,title,client_name,status,event_date,created_at_utc,assigned_to,job_lead,client_id)
                values($1,$2,$3,$4,$5,$6,$7,now()-($8*interval '1 day'),$9,$9,$10) on conflict(id) do nothing",
                job,workspace,conversation,Titles[i],Clients[i],Stages[i],DateOnly.FromDateTime(DateTime.UtcNow.AddDays(i-5)),i+1,(object?)owner??DBNull.Value,client);
            Exec(c,tx,"update studio_conversations set job_id=$2 where id=$1 and job_id is null",conversation,job);
            // Give monitoring meaningful elapsed times rather than fifteen
            // identical just-created status lights.
            Exec(c,tx,@"update studio_jobs set stage_entered_at_utc=now()-($2*interval '1 hour')
                where id=$1 and stage_entered_at_utc>now()-interval '1 hour'",job,(i%5+1)*5);
            Exec(c,tx,@"update studio_workflow_history set entered_at_utc=
                (select stage_entered_at_utc from studio_jobs where id=$1)
                where job_id=$1 and exited_at_utc is null",job);

            // Front Desk and Finance see the same Job rather than a detached
            // demonstration card. A fictional booking and its verified receipt
            // share a deterministic reference with the workflow Job.
            var requestId=Id("booking:"+i);
            var payload=System.Text.Json.JsonSerializer.Serialize(new {
                workspaceId=workspace,clientName=Clients[i],clientEmail=email,
                title=Titles[i],eventDate=DateOnly.FromDateTime(DateTime.UtcNow.AddDays(i-5)),
                requiredDeposit=5000,currency="JMD",bookingSource="Demo",
                externalBookingId="CONNECTED-DEMO-"+i
            });
            Exec(c,tx,@"insert into studio_frontdesk_requests(id,workspace_id,payload,status,job_id,created_by)
                values($1,$2,$3::jsonb,$4,$5,$6) on conflict(id) do nothing",
                requestId,workspace,payload,i==0?"Pending":"Confirmed",job,actor);
            if(i>0 && i<14)
                Exec(c,tx,@"insert into studio_frontdesk_deposits(id,request_id,amount,currency,provider,reference,recorded_by)
                    values($1,$2,5000,'JMD','demo','CONNECTED-DEMO-'||$3,$4) on conflict(id) do nothing",
                    Id("deposit:"+i),requestId,i.ToString(),actor);

            Exec(c,tx,@"insert into studio_messages(id,conversation_id,author_display_name,visibility,body,created_at_utc)
                values($1,$2,$3,'External',$4,now()-($5*interval '1 day')) on conflict(id) do nothing",
                Id("message:"+i),conversation,Clients[i],"Demo enquiry: please confirm availability and next steps for "+Titles[i]+".",i+1);
            if(i>=2 && i<=13)
            {
                var session=Id("session:"+i);
                Exec(c,tx,@"insert into studio_workflow_sessions(id,job_id,title,required,crew_finalized)
                    values($1,$2,'Main photography session',true,true) on conflict(id) do nothing",session,job);
                Exec(c,tx,@"insert into studio_workflow_crew(session_id,user_name,role,assigned_by,ready)
                    values($1,$2,'Lead',$2,false) on conflict(session_id,user_name) do nothing",session,actor);
                if(i>=4)
                {
                    var entry=Id("schedule:"+i);
                    var shoot=Id("shoot:"+i);
                    var start=DateTimeOffset.UtcNow.AddDays(i==4?3:-(i-3));
                    var finish=start.AddHours(2);
                    Exec(c,tx,@"insert into studio_schedule_entries(id,job_id,title,starts_at_utc,ends_at_utc,location,status,notes,created_at_utc)
                        values($1,$2,$3,$4,$5,'Kingston, Jamaica','Confirmed','Connected Workflow demo schedule',now()) on conflict(id) do nothing",
                        entry,job,Titles[i],start,finish);
                    // The session is created by Connected Workflow; avoid the legacy
                    // compatibility trigger replacing its crew assignments.
                    Exec(c,tx,"select set_config('studio.connected_command','true',true)");
                    Exec(c,tx,@"insert into studio_shoot_sessions(id,job_id,schedule_entry_id,photographer_user_name,call_time_utc,planned_end_utc,location,status,created_at_utc,updated_at_utc)
                        values($1,$2,$3,$4,$5,$6,'Kingston, Jamaica',$7,now(),now()) on conflict(id) do nothing",
                        shoot,job,entry,actor,start,finish,i==4?"Scheduled":i==5?"InProgress":"Completed");
                    Exec(c,tx,"update studio_workflow_sessions set shoot_id=$2 where id=$1 and shoot_id is null",session,shoot);
                }
            }
            if(i>=8 && i<=13)
                Exec(c,tx,@"insert into studio_job_activities(id,job_id,activity_type,title,status,priority,assigned_to,created_by,created_at_utc,updated_at_utc)
                    values($1,$2,'Edit','Review and finish production','NotStarted','Normal',$3,$3,now(),now()) on conflict(id) do nothing",Id("task:"+i),job,actor);
            created++;
        }
        return new {jobs=created,workspaces=WorkspaceNames.Length,stages=Stages.Length,message="Connected Workflow demo records generated. All contacts use example.invalid; no real client addresses."};
    }

    internal static object Reset(StudioDatabase db)
    {
        using var c=db.OpenConnection();
        using var tx=c.BeginTransaction();
        Lock(c,tx);
        var result=ResetData(c,tx);
        tx.Commit();
        return result;
    }

    internal static object ResetAndGenerate(StudioDatabase db,string actor)
    {
        using var c=db.OpenConnection();
        using var tx=c.BeginTransaction();
        Lock(c,tx);
        var removed=ResetData(c,tx);
        var generated=GenerateData(c,tx,actor);
        tx.Commit();
        return new {removed,generated,message="Studio business data reset and Connected Workflow demonstration generated atomically."};
    }

    private static object ResetData(NpgsqlConnection c,NpgsqlTransaction tx)
    {
        // This is the database phase only. The public factory-reset action MUST NOT
        // call it until Studio-owned external storage has been emptied and verified.
        // In particular, a database transaction cannot roll back an object-store delete.
        var protectedTables=new HashSet<string>(StringComparer.Ordinal)
        {
            "studio_workspaces","studio_state","studio_user_profiles","studio_employees",
            "studio_workflow_thresholds","studio_workflow_audiences",
            "studio_workflow_migrations","studio_global_operational_roles",
            "studio_global_operational_role_members"
        };
        var tables=new HashSet<string>(StringComparer.Ordinal);
        using(var q=new NpgsqlCommand(@"select tablename from pg_tables
            where schemaname='public' and tablename like 'studio_%'",c,tx))
        using(var reader=q.ExecuteReader())
            while(reader.Read())
            {
                var table=reader.GetString(0);
                if(!protectedTables.Contains(table))tables.Add(table);
            }

        // PostgreSQL will reject TRUNCATE if any foreign-key child is omitted.
        // Explicitly reject protected or non-Studio dependencies rather than CASCADE.
        using(var q=new NpgsqlCommand(@"select child.relname,parent.relname
            from pg_constraint fk join pg_class child on child.oid=fk.conrelid
            join pg_class parent on parent.oid=fk.confrelid
            join pg_namespace ns on ns.oid=child.relnamespace
            where fk.contype='f' and ns.nspname='public'",c,tx))
        using(var reader=q.ExecuteReader())
            while(reader.Read())
            {
                var child=reader.GetString(0);
                var parent=reader.GetString(1);
                if(tables.Contains(parent)&&!tables.Contains(child))
                    throw new InvalidOperationException(
                        "Factory reset blocked by protected dependency: "+child+" -> "+parent);
            }

        // Workspace memberships and seed-run markers are business/demo records.
        // Never truncate the system workspace itself: its immutable identity is
        // enforced by the protect_unassigned trigger.
        if(tables.Count>0)
        {
            var names=string.Join(",",tables.Order(StringComparer.Ordinal).Select(name=>
            {
                if(!System.Text.RegularExpressions.Regex.IsMatch(name,@"^studio_[a-z0-9_]+$"))
                    throw new InvalidOperationException("Unsafe database identifier: "+name);
                return "public."+name;
            }));
            Exec(c,tx,"truncate table "+names+" restart identity");
        }
        Exec(c,tx,@"delete from studio_workspaces
            where system_key is distinct from 'unassigned'");
        using(var check=new NpgsqlCommand(@"select count(*) from studio_workspaces
            where system_key='unassigned' and name='Unassigned' and is_enabled",c,tx))
            if(Convert.ToInt64(check.ExecuteScalar())!=1)
                throw new InvalidOperationException("Factory reset requires exactly one permanent Unassigned workspace.");
        using(var check=new NpgsqlCommand("select count(*) from studio_workspaces",c,tx))
            if(Convert.ToInt64(check.ExecuteScalar())!=1)
                throw new InvalidOperationException("Factory reset left unexpected workspaces.");

        // studio_state also holds persistent application configuration; clear
        // only seed bookkeeping, never the whole table.
        Exec(c,tx,@"delete from studio_state where key like 'demo-data-%'
            or key like 'studio-demo-%'");
        return new {clearedTables=tables.Count,workspacesRemaining=1,
            message="Studio database business data cleared; only Unassigned remains. External storage must be independently purged and verified."};
    }

    private static void Lock(NpgsqlConnection c,NpgsqlTransaction tx)
        =>Exec(c,tx,"select pg_advisory_xact_lock(hashtextextended('studio-connected-demo-v1',0))");

    private static void Exec(NpgsqlConnection c,NpgsqlTransaction tx,string sql,params object[] args)
    {
        using var q=new NpgsqlCommand(sql,c,tx);
        foreach(var arg in args)q.Parameters.AddWithValue(arg);
        q.ExecuteNonQuery();
    }
}
