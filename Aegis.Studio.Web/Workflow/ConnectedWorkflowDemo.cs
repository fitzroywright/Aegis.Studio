using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Workflow;

// Dependency-aware deletion; generation is owned solely by DemoSeedCoordinator.
internal static class ConnectedWorkflowDemo
{
    internal static void ValidateReset(StudioDatabase db)
    {
        using var c=db.OpenConnection();
        using var tx=c.BeginTransaction();
        Lock(c,tx);
        ResetData(c,tx);
        tx.Rollback();
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
            "studio_global_operational_role_members","studio_global_operational_role_permissions",
            "studio_directory_group_roles","studio_security_roles","studio_demo_seed_runs",
            // Job roles are configurable application data required before worker seeding.
            "studio_worker_types"
        };
        string schema;
        using(var current=new NpgsqlCommand("select current_schema()",c,tx))schema=(string)current.ExecuteScalar()!;
        var tables=new HashSet<string>(StringComparer.Ordinal);
        using(var q=new NpgsqlCommand(@"select tablename from pg_tables
            where schemaname=$1 and tablename like 'studio_%'",c,tx)){q.Parameters.AddWithValue(schema);

        using(var reader=q.ExecuteReader())
            while(reader.Read())
            {
                var table=reader.GetString(0);
                if(!protectedTables.Contains(table))tables.Add(table);
            }
        }

        // PostgreSQL will reject TRUNCATE if any foreign-key child is omitted.
        // Explicitly reject protected or non-Studio dependencies rather than CASCADE.
        using(var q=new NpgsqlCommand(@"select child.relname,parent.relname
            from pg_constraint fk join pg_class child on child.oid=fk.conrelid
            join pg_class parent on parent.oid=fk.confrelid
            join pg_namespace ns on ns.oid=child.relnamespace
            where fk.contype='f' and ns.nspname=$1",c,tx)){q.Parameters.AddWithValue(schema);

        using(var reader=q.ExecuteReader())
            while(reader.Read())
            {
                var child=reader.GetString(0);
                var parent=reader.GetString(1);
                if(tables.Contains(parent)&&!tables.Contains(child))
                    throw new InvalidOperationException(
                        "Factory reset blocked by protected dependency: "+child+" -> "+parent);
            }
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
                return new NpgsqlCommandBuilder().QuoteIdentifier(schema)+"."+new NpgsqlCommandBuilder().QuoteIdentifier(name);
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

        Exec(c,tx,"insert into studio_state(key,value,updated_at_utc) values('studio-dataset-epoch',$1,now()) on conflict(key) do update set value=excluded.value,updated_at_utc=excluded.updated_at_utc",Guid.NewGuid().ToString("D"));

        // Common.Messaging uses shared tables: delete only this application's queue.
        foreach (string table in new[] { "common_messaging_queue", "common_messaging_delivery_receipts" })
        {
            using var exists = new NpgsqlCommand("select to_regclass($1) is not null", c, tx);
            exists.Parameters.AddWithValue(table);
            if ((bool)exists.ExecuteScalar()!) Exec(c,tx,"delete from "+table+" where queue_name='aegis-studio'");
        }

        // studio_state also holds persistent application configuration; clear
        // only seed bookkeeping, never the whole table.
        Exec(c,tx,@"delete from studio_state where key like 'demo-data-%'
            or key like 'studio-demo-%' or key like 'demo-job-workflow-graphs-%'");
        using(var exists=new NpgsqlCommand("select to_regclass('studio_demo_seed_runs') is not null",c,tx))
            if((bool)exists.ExecuteScalar()!)Exec(c,tx,"delete from studio_demo_seed_runs where status <> 'Running'");
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

