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
            Exec(c,tx,@"insert into studio_messages(id,conversation_id,author_display_name,visibility,body,created_at_utc)
                values($1,$2,$3,'External',$4,now()-($5*interval '1 day')) on conflict(id) do nothing",
                Id("message:"+i),conversation,Clients[i],"Demo enquiry: please confirm availability and next steps for "+Titles[i]+".",i+1);
            if(i>=2 && i<=13)
            {
                var session=Id("session:"+i);
                Exec(c,tx,@"insert into studio_workflow_sessions(id,job_id,title,required,crew_finalized)
                    values($1,$2,'Main photography session',true,true) on conflict(id) do nothing",session,job);
                Exec(c,tx,@"insert into studio_workflow_crew(session_id,user_name,role,assigned_by,ready)
                    values($1,$2,'Lead',$2,$3) on conflict(session_id,user_name) do nothing",session,actor,i>=6);
            }
            if(i>=8 && i<=13)
                Exec(c,tx,@"insert into studio_job_activities(id,job_id,activity_type,title,status,priority,assigned_to,created_by,created_at_utc,updated_at_utc)
                    values($1,$2,'Edit','Review and finish production','NotStarted','Normal',$3,$3,now(),now()) on conflict(id) do nothing",Id("task:"+i),job,actor);
            created++;
        }
        tx.Commit();
        return new {jobs=created,workspaces=WorkspaceNames.Length,stages=Stages.Length,message="Connected Workflow demo records generated. All contacts use example.invalid; no real client addresses."};
    }

    internal static object Reset(StudioDatabase db)
    {
        using var c=db.OpenConnection();
        using var tx=c.BeginTransaction();
        Lock(c,tx);
        // Reset only business records, and every table that has a direct or
        // indirect foreign key into them. PostgreSQL TRUNCATE checks the
        // entire dependency set atomically. Protected configuration must never
        // be silently added to the reset.
        var roots=new[]{"studio_jobs","studio_clients","studio_conversations"};
        var tables=new HashSet<string>(roots,StringComparer.Ordinal);
        var protectedTables=new HashSet<string>(StringComparer.Ordinal)
        {
            "studio_workspaces","studio_workspace_members","studio_user_profiles",
            "studio_employees","studio_state","studio_demo_seed_runs",
            "studio_workflow_thresholds","studio_workflow_audiences",
            "studio_workflow_migrations","studio_global_operational_roles",
            "studio_global_operational_role_members"
        };
        bool changed;
        do
        {
            changed=false;
            using var q=new NpgsqlCommand(@"select child.relname,parent.relname
                from pg_constraint fk join pg_class child on child.oid=fk.conrelid
                join pg_class parent on parent.oid=fk.confrelid
                join pg_namespace ns on ns.oid=child.relnamespace
                where fk.contype='f' and ns.nspname='public'",c,tx);
            using var r=q.ExecuteReader();
            var discovered=new List<string>();
            while(r.Read())
            {
                string child=r.GetString(0),parent=r.GetString(1);
                if(tables.Contains(parent)&&!tables.Contains(child))discovered.Add(child);
            }
            foreach(string child in discovered)
            {
                if(!child.StartsWith("studio_",StringComparison.Ordinal)||protectedTables.Contains(child))
                    throw new InvalidOperationException("Reset blocked: protected dependent table "+child);
                changed|=tables.Add(child);
            }
        }while(changed);
        // Quote every name obtained from PostgreSQL's catalog; never concatenate
        // user input into an identifier.
        var tableList=string.Join(",",tables.Order(StringComparer.Ordinal)
            .Select(name=>"public.\""+name.Replace("\"","\"\"")+"\""));
        Exec(c,tx,"truncate table "+tableList+" restart identity");
        tx.Commit();
        return new {clearedTables=tables.Count,message="Studio business records and their database dependents cleared. Workspace/security configuration was preserved; external storage objects were not deleted."};
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
