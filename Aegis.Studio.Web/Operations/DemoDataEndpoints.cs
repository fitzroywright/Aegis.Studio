using System.Security.Claims;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Media;
using Microsoft.AspNetCore.Authorization;
using Npgsql;
using Common.Storage;
using Aegis.Studio.Web.Workflow;

namespace Aegis.Studio.Web.Operations;

public static class DemoDataEndpoints
{
    public static void MapStudioDemoData(this WebApplication app)
    {
        app.MapPost("/api/admin/demo/seed", (HttpContext context, DemoResetConfirmation request, IWebHostEnvironment environment, DemoSeedCoordinator coordinator) =>
        {
            bool demoDataEnabled = environment.IsDevelopment()
                || app.Configuration.GetValue("DemoData:Enabled", false);
            if (!demoDataEnabled) return Results.NotFound();

            string? login = context.User.FindFirstValue("login_name")
                ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(login))
                return Results.BadRequest(new { error = "A login identifier is required." });

            if(request.Confirmation!="REMOVE STUDIO DEMO DATA") return Results.BadRequest(new { error="Confirm clearing and recreating demo data." });
            DemoSeedRunStatus run = coordinator.Start(login);
            return Results.Accepted($"/api/admin/demo/seed/{run.RunId}", run);
        }).RequireAuthorization(new AuthorizeAttribute { Roles = "Administrator" });

        app.MapGet("/api/admin/demo/seed/{runId:guid}", (Guid runId, IWebHostEnvironment environment, DemoSeedCoordinator coordinator) =>
        {
            bool demoDataEnabled = environment.IsDevelopment()
                || app.Configuration.GetValue("DemoData:Enabled", false);
            if (!demoDataEnabled) return Results.NotFound();

            DemoSeedRunStatus? run = coordinator.Get(runId);
            return run is null ? Results.NotFound() : Results.Ok(run);
        }).RequireAuthorization(new AuthorizeAttribute { Roles = "Administrator" });

        app.MapGet("/api/admin/demo/seed/current", (IWebHostEnvironment environment, DemoSeedCoordinator coordinator) =>
        {
            bool demoDataEnabled = environment.IsDevelopment()
                || app.Configuration.GetValue("DemoData:Enabled", false);
            if (!demoDataEnabled) return Results.NotFound();

            DemoSeedRunStatus? run = coordinator.Current;
            return run is null ? Results.NoContent() : Results.Ok(run);
        }).RequireAuthorization(new AuthorizeAttribute { Roles = "Administrator" });
    }

    internal static string Seed(StudioDatabase database, IFileStorage storage, string login, Action<DemoSeedProgress>? report = null)
    {
        report?.Invoke(new(0, "Preparing", 0, 0, "Connecting to the database."));

        using (var empty = database.OpenConnection())
        using (var check = new NpgsqlCommand("select exists(select 1 from studio_jobs)", empty))
            if ((bool)check.ExecuteScalar()!) throw new InvalidOperationException("Clear the demo dataset before recreating it. In-place demo repair is not supported.");

        DemoDataExpansion.PreparePhotos(report);
        DemoDataExpansion.PrepareStaffPhotos(report);
        MediaProductionEndpoints.EnsureWorkspaceCoverSchema(database);
        var accessRoles = new GlobalOperationalRoleStore(database);
        accessRoles.EnsureSchema();
        accessRoles.EnsureAdministrator(login);

        // Phase 1: create the high-volume operational workload in small, durable batches.
        // Each batch owns only deterministic DEMO ids, so retries are safe and do not
        // duplicate data. A later media/network failure cannot roll this work back.
        int denseJobs = 0;
        for (int batch = 0; batch < DemoDenseScheduleData.BatchCount; batch++)
        {
            using var batchConnection = database.OpenConnection();
            using var batchTransaction = batchConnection.BeginTransaction();
            using (var batchLock = new NpgsqlCommand("select pg_advisory_xact_lock(72709128)", batchConnection, batchTransaction))
                batchLock.ExecuteNonQuery();

            denseJobs += DemoDenseScheduleData.SeedBatch(batchConnection, batchTransaction, login, batch, report);
            batchTransaction.Commit();
        }

        // Phase 2: keep the richer media demonstration, but isolate it from
        // the operational batches above. If a public photo host or storage provider is
        // unavailable, rerunning the loader preserves and resumes the durable workload.
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "select set_config('studio.demo_seed','true',true); select pg_advisory_xact_lock(72709128)";
        report?.Invoke(new(26, "Rich demo data", 0, 0, "Loading demo scenarios and media separately from the operational schedule."));
        command.ExecuteNonQuery();

        int statementNumber = 0;
        foreach (string statement in SeedStatements)
        {
            using var seedCommand = connection.CreateCommand();
            seedCommand.Transaction = transaction;
            string sql = statement.Trim();
            if (sql.StartsWith("insert into ", StringComparison.OrdinalIgnoreCase))
                sql = sql.TrimEnd(';') + " on conflict do nothing;";
            seedCommand.CommandText = sql;
            if (statement.Contains("$1", StringComparison.Ordinal)) seedCommand.Parameters.AddWithValue(login);
            seedCommand.ExecuteNonQuery();
            statementNumber++;
            report?.Invoke(new(28 + 2 * statementNumber / SeedStatements.Length, "Base records", statementNumber, SeedStatements.Length, "Creating demo workspaces, conversations and jobs."));
        }

        CreateDemoWorkspaces(connection, transaction, login);
        bool expanded = DemoDataExpansion.Seed(connection, transaction, login, storage, report);
        report?.Invoke(new(30, "Presentation records", 0, 0, "Linking wedding people, tasks and announcements."));
        bool presentationExpanded = DemoPresentationData.Seed(connection, transaction, login);
        bool fullStudioExpanded = DemoFullStudioData.Seed(connection, transaction, login, storage, report);
        DemoIntegratedWorkflow.Seed(connection,transaction,login,storage,report);
        DemoJobWorkflowGraphs.Seed(connection,transaction,login);
        SeedWorkspaceCovers(connection,transaction,report);
        report?.Invoke(new(96, "Committing rich demo", 0, 0, "Saving historical workflow and media data."));
        transaction.Commit();
        report?.Invoke(new(97, "Booking catalog", 0, 0, "Adding Bryan Studios services, session types and opening hours."));
        DemoBookingCatalog.Seed(database);
        DemoJobPhotoPopulation.Seed(database,storage,login,report);
        DemoDatasetFinalizer.Complete(database,storage,login,report);
        SeedDistinctStaffPhotos(database,report);

        return fullStudioExpanded
            ? $"Operational demo ready with {denseJobs} jobs over {DemoDenseScheduleData.Days} days across all six workspaces, plus the full Studio media demonstration."
            : presentationExpanded
                ? $"Operational demo ready with {denseJobs} jobs over {DemoDenseScheduleData.Days} days. Demo presentation data also loaded."
                : expanded
                    ? $"Operational demo ready with {denseJobs} jobs over {DemoDenseScheduleData.Days} days. Legacy demo media also expanded."
                    : $"Operational demo ready with {denseJobs} jobs over {DemoDenseScheduleData.Days} days; existing rich demo data is already up to date.";
    }

    private static readonly (string Category,int Variant)[] WorkspaceCoverSources =
    [
        ("people",0),
        ("wedding",0),
        ("graduation",0),
        ("graduation",1),
        ("product",0),
        ("sports",0)
    ];

    private static void SeedWorkspaceCovers(NpgsqlConnection connection,NpgsqlTransaction transaction,Action<DemoSeedProgress>? report)
    {
        var workspaces=new List<(Guid Id,string Name)>();
        using(var query=new NpgsqlCommand("select id,name from studio_workspaces where system_key is distinct from 'unassigned' order by sort_order,name",connection,transaction))
        using(var reader=query.ExecuteReader())
            while(reader.Read())workspaces.Add((reader.GetGuid(0),reader.GetString(1)));

        using(var clearUnassigned=new NpgsqlCommand("delete from studio_workspace_covers where workspace_id in (select id from studio_workspaces where system_key='unassigned')",connection,transaction))
            clearUnassigned.ExecuteNonQuery();

        for(int index=0;index<workspaces.Count;index++)
        {
            var source=WorkspaceCoverSources[index%WorkspaceCoverSources.Length];
            string[] files=DemoDataExpansion.LocalPhotos(source.Category);
            if(files.Length==0)throw new InvalidOperationException($"No demo workspace photograph is available for category '{source.Category}'.");
            string file=files[source.Variant%files.Length];
            using var decoded=StudioImageProcessor.DecodeOriented(File.OpenRead(file));
            using var resized=StudioImageProcessor.ResizeMax(decoded,1600);
            byte[] content=StudioImageProcessor.EncodeJpeg(resized,90);
            using var save=new NpgsqlCommand("""
                insert into studio_workspace_covers(workspace_id,content_type,image_data,updated_at_utc)
                values($1,'image/jpeg',$2,now())
                on conflict(workspace_id) do update set
                  content_type=excluded.content_type,
                  image_data=excluded.image_data,
                  updated_at_utc=now()
                """,connection,transaction);
            save.Parameters.AddWithValue(workspaces[index].Id);
            save.Parameters.AddWithValue(content);
            save.ExecuteNonQuery();
            report?.Invoke(new(94,"Linking workspace photographs",index+1,workspaces.Count,$"Assigned a photograph to {workspaces[index].Name}."));
        }
    }

    private static void SeedDistinctStaffPhotos(StudioDatabase database, Action<DemoSeedProgress>? report)
    {
        var employees=new StudioEmployeeDirectoryStore(database);
        employees.EnsureSchema();
        string[] files=DemoDataExpansion.StaffPhotos();
        var staff=employees.GetAll().Where(person=>person.UserName.StartsWith("demo.",StringComparison.OrdinalIgnoreCase)).OrderBy(person=>person.UserName,StringComparer.OrdinalIgnoreCase).ToArray();
        if(files.Length<staff.Length)throw new InvalidOperationException($"The staff photo library contains {files.Length} unique images but the demo has {staff.Length} staff users.");
        var used=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for(int index=0;index<staff.Length;index++)
        {
            string file=files[index];
            if(!used.Add(file))throw new InvalidOperationException($"Staff photo '{file}' was selected more than once.");
            var photo=DemoDataExpansion.ReadStaffPhoto(file);
            employees.SavePhoto(staff[index].UserName,"DemoStaff",photo.ContentType,photo.FileName,photo.Content);
            report?.Invoke(new(95,"Linking staff profiles",index+1,staff.Length,$"Assigned a unique profile photo to {staff[index].DisplayName}."));
        }
    }

    private static void CreateDemoWorkspaces(NpgsqlConnection connection, NpgsqlTransaction transaction, string login)
    {
        const string workspaceSql = """
            insert into studio_workspaces(id,name,email_address,route_key,is_enabled,sort_order,created_at_utc,updated_at_utc)
            values
              ('de000000-0000-4000-0001-000000000001','DEMO - Portraits and IDs',null,'studio-demo-v1-1',true,101,now(),now()),
              ('de000000-0000-4000-0001-000000000002','DEMO - Weddings and Events',null,'studio-demo-v1-2',true,102,now(),now()),
              ('de000000-0000-4000-0001-000000000003','DEMO - Schools and Corporate',null,'studio-demo-v1-3',true,103,now(),now())
            on conflict(id) do update set
              name=excluded.name,
              route_key=excluded.route_key,
              is_enabled=true,
              sort_order=excluded.sort_order,
              updated_at_utc=now();
            """;
        using (var workspace = new NpgsqlCommand(workspaceSql, connection, transaction))
            workspace.ExecuteNonQuery();

        const string memberSql = """
            insert into studio_workspace_members
            (workspace_id,user_name,display_name,is_active,access_workspace,can_view,reply_requester,view_attachments,add_attachments,create_job,edit_job,manage_schedule,manage_production,notify_members,edit_workspace,manage_members,admin_workspace,send_as_self,created_at_utc,updated_at_utc)
            values
              ('de000000-0000-4000-0001-000000000001',$1,$1,true,true,true,true,true,true,true,true,true,true,true,true,true,true,false,now(),now()),
              ('de000000-0000-4000-0001-000000000002',$1,$1,true,true,true,true,true,true,true,true,true,true,true,true,true,true,false,now(),now()),
              ('de000000-0000-4000-0001-000000000003',$1,$1,true,true,true,true,true,true,true,true,true,true,true,true,true,true,false,now(),now())
            on conflict(workspace_id,user_name) do update set
              display_name=excluded.display_name,
              is_active=true,
              access_workspace=true,
              can_view=true,
              reply_requester=true,
              view_attachments=true,
              add_attachments=true,
              create_job=true,
              edit_job=true,
              manage_schedule=true,
              manage_production=true,
              notify_members=true,
              edit_workspace=true,
              manage_members=true,
              admin_workspace=true,
              updated_at_utc=now();
            """;
        using var member = new NpgsqlCommand(memberSql, connection, transaction);
        member.Parameters.AddWithValue(login);
        member.ExecuteNonQuery();
    }

    private static readonly string[] SeedStatements =
    [
        """insert into studio_workspaces(id,name,email_address,route_key,is_enabled,sort_order,created_at_utc,updated_at_utc) values('de000000-0000-4000-0001-000000000001','DEMO - Portraits and IDs',null,'studio-demo-v1-1',true,101,now(),now());""",
        """insert into studio_workspace_members(workspace_id,user_name,display_name,is_active,access_workspace,can_view,reply_requester,view_attachments,add_attachments,create_job,edit_job,manage_schedule,manage_production,notify_members,edit_workspace,manage_members,admin_workspace,send_as_self,created_at_utc,updated_at_utc) values('de000000-0000-4000-0001-000000000001',$1,$1,true,true,true,true,true,true,true,true,true,true,true,true,true,true,false,now(),now());""",
        """insert into studio_workspaces(id,name,email_address,route_key,is_enabled,sort_order,created_at_utc,updated_at_utc) values('de000000-0000-4000-0001-000000000002','DEMO - Weddings and Events',null,'studio-demo-v1-2',true,102,now(),now());""",
        """insert into studio_workspace_members(workspace_id,user_name,display_name,is_active,access_workspace,can_view,reply_requester,view_attachments,add_attachments,create_job,edit_job,manage_schedule,manage_production,notify_members,edit_workspace,manage_members,admin_workspace,send_as_self,created_at_utc,updated_at_utc) values('de000000-0000-4000-0001-000000000002',$1,$1,true,true,true,true,true,true,true,true,true,true,true,true,true,true,false,now(),now());""",
        """insert into studio_workspaces(id,name,email_address,route_key,is_enabled,sort_order,created_at_utc,updated_at_utc) values('de000000-0000-4000-0001-000000000003','DEMO - Schools and Corporate',null,'studio-demo-v1-3',true,103,now(),now());""",
        """insert into studio_workspace_members(workspace_id,user_name,display_name,is_active,access_workspace,can_view,reply_requester,view_attachments,add_attachments,create_job,edit_job,manage_schedule,manage_production,notify_members,edit_workspace,manage_members,admin_workspace,send_as_self,created_at_utc,updated_at_utc) values('de000000-0000-4000-0001-000000000003',$1,$1,true,true,true,true,true,true,true,true,true,true,true,true,true,true,false,now(),now());""",
        """insert into studio_conversations(id,workspace_id,subject,contact_name,contact_address,channel,is_closed,created_at_utc) values('de000000-0000-4000-0002-000000000001','de000000-0000-4000-0001-000000000001','DEMO - Family portrait enquiry','Morgan Family','demo-client-1@example.invalid','Manual',false,now()-interval '1 days');""",
        """insert into studio_messages(id,conversation_id,author_display_name,visibility,body,created_at_utc) values('de000000-0000-4000-0004-000000000001','de000000-0000-4000-0002-000000000001','Morgan Family','External','Fictional demo enquiry: Family portrait enquiry. Please confirm availability, package options and delivery dates.',now()-interval '1 days');""",
        """insert into studio_jobs(id,workspace_id,conversation_id,title,client_name,status,event_date,created_at_utc,assigned_to,job_lead) values('de000000-0000-4000-0003-000000000001','de000000-0000-4000-0001-000000000001','de000000-0000-4000-0002-000000000001','DEMO - Family portrait enquiry','Morgan Family','New',current_date+-3,now()-interval '1 days',$1,$1);""",
        """update studio_conversations set job_id='de000000-0000-4000-0003-000000000001' where id='de000000-0000-4000-0002-000000000001';""",
        """insert into studio_financial_events(id,job_id,kind,amount,note,created_at_utc) values('de000000-0000-4000-0005-000000000001','de000000-0000-4000-0003-000000000001','Deposit',2500,'DEMO ONLY - fictional deposit, no payment processed',now());""",
        """insert into studio_schedule_entries(id,job_id,title,starts_at_utc,ends_at_utc,location,status,notes,created_at_utc) values('de000000-0000-4000-0006-000000000001','de000000-0000-4000-0003-000000000001','DEMO - Family portrait enquiry',date_trunc('day',now())+interval '-3 days 15 hours',date_trunc('day',now())+interval '-3 days 17 hours','Demo studio','Scheduled','Fictional demo appointment',now());""",
        """insert into studio_job_activities(id,job_id,activity_type,title,description,status,priority,assigned_to,due_at_utc,started_at_utc,completed_at_utc,created_by,created_at_utc,updated_at_utc) values('de000000-0000-4000-0007-000000000001','de000000-0000-4000-0003-000000000001','Other','DEMO - Edit and prepare delivery','Select images, retouch and prepare client delivery.','NotStarted','Normal',$1,now()+interval '-3 days',null,null,$1,now(),now());""",
        """insert into studio_conversations(id,workspace_id,subject,contact_name,contact_address,channel,is_closed,created_at_utc) values('de000000-0000-4000-0002-000000000002','de000000-0000-4000-0001-000000000001','DEMO - Passport photo session','Taylor Passport','demo-client-2@example.invalid','Manual',false,now()-interval '2 days');""",
        """insert into studio_messages(id,conversation_id,author_display_name,visibility,body,created_at_utc) values('de000000-0000-4000-0004-000000000002','de000000-0000-4000-0002-000000000002','Taylor Passport','External','Fictional demo enquiry: Passport photo session. Please confirm availability, package options and delivery dates.',now()-interval '2 days');""",
        """insert into studio_jobs(id,workspace_id,conversation_id,title,client_name,status,event_date,created_at_utc,assigned_to,job_lead) values('de000000-0000-4000-0003-000000000002','de000000-0000-4000-0001-000000000001','de000000-0000-4000-0002-000000000002','DEMO - Passport photo session','Taylor Passport','Scheduled',current_date+-2,now()-interval '2 days',$1,$1);""",
        """update studio_conversations set job_id='de000000-0000-4000-0003-000000000002' where id='de000000-0000-4000-0002-000000000002';""",
        """insert into studio_financial_events(id,job_id,kind,amount,note,created_at_utc) values('de000000-0000-4000-0005-000000000002','de000000-0000-4000-0003-000000000002','Deposit',5000,'DEMO ONLY - fictional deposit, no payment processed',now());""",
        """insert into studio_schedule_entries(id,job_id,title,starts_at_utc,ends_at_utc,location,status,notes,created_at_utc) values('de000000-0000-4000-0006-000000000002','de000000-0000-4000-0003-000000000002','DEMO - Passport photo session',date_trunc('day',now())+interval '-2 days 15 hours',date_trunc('day',now())+interval '-2 days 17 hours','Demo studio','Scheduled','Fictional demo appointment',now());""",
        """insert into studio_job_activities(id,job_id,activity_type,title,description,status,priority,assigned_to,due_at_utc,started_at_utc,completed_at_utc,created_by,created_at_utc,updated_at_utc) values('de000000-0000-4000-0007-000000000002','de000000-0000-4000-0003-000000000002','Other','DEMO - Edit and prepare delivery','Select images, retouch and prepare client delivery.','NotStarted','Normal',$1,now()+interval '-2 days',null,null,$1,now(),now());""",
        """insert into studio_conversations(id,workspace_id,subject,contact_name,contact_address,channel,is_closed,created_at_utc) values('de000000-0000-4000-0002-000000000003','de000000-0000-4000-0001-000000000001','DEMO - Graduation portraits','Jordan Graduation','demo-client-3@example.invalid','Manual',false,now()-interval '3 days');""",
        """insert into studio_messages(id,conversation_id,author_display_name,visibility,body,created_at_utc) values('de000000-0000-4000-0004-000000000003','de000000-0000-4000-0002-000000000003','Jordan Graduation','External','Fictional demo enquiry: Graduation portraits. Please confirm availability, package options and delivery dates.',now()-interval '3 days');""",
        """insert into studio_conversations(id,workspace_id,subject,contact_name,contact_address,channel,is_closed,created_at_utc) values('de000000-0000-4000-0002-000000000004','de000000-0000-4000-0001-000000000002','DEMO - Wedding coverage','Campbell Wedding','demo-client-4@example.invalid','Manual',false,now()-interval '4 days');""",
        """insert into studio_messages(id,conversation_id,author_display_name,visibility,body,created_at_utc) values('de000000-0000-4000-0004-000000000004','de000000-0000-4000-0002-000000000004','Campbell Wedding','External','Fictional demo enquiry: Wedding coverage. Please confirm availability, package options and delivery dates.',now()-interval '4 days');""",
        """insert into studio_jobs(id,workspace_id,conversation_id,title,client_name,status,event_date,created_at_utc,assigned_to,job_lead) values('de000000-0000-4000-0003-000000000004','de000000-0000-4000-0001-000000000002','de000000-0000-4000-0002-000000000004','DEMO - Wedding coverage','Campbell Wedding','Photography',current_date+0,now()-interval '4 days',$1,$1);""",
        """update studio_conversations set job_id='de000000-0000-4000-0003-000000000004' where id='de000000-0000-4000-0002-000000000004';""",
        """insert into studio_financial_events(id,job_id,kind,amount,note,created_at_utc) values('de000000-0000-4000-0005-000000000004','de000000-0000-4000-0003-000000000004','Deposit',10000,'DEMO ONLY - fictional deposit, no payment processed',now());""",
        """insert into studio_schedule_entries(id,job_id,title,starts_at_utc,ends_at_utc,location,status,notes,created_at_utc) values('de000000-0000-4000-0006-000000000004','de000000-0000-4000-0003-000000000004','DEMO - Wedding coverage',date_trunc('day',now())+interval '0 days 15 hours',date_trunc('day',now())+interval '0 days 17 hours','Demo studio','Scheduled','Fictional demo appointment',now());""",
        """insert into studio_job_activities(id,job_id,activity_type,title,description,status,priority,assigned_to,due_at_utc,started_at_utc,completed_at_utc,created_by,created_at_utc,updated_at_utc) values('de000000-0000-4000-0007-000000000004','de000000-0000-4000-0003-000000000004','Other','DEMO - Edit and prepare delivery','Select images, retouch and prepare client delivery.','NotStarted','Normal',$1,now()+interval '0 days',null,null,$1,now(),now());""",
        """insert into studio_conversations(id,workspace_id,subject,contact_name,contact_address,channel,is_closed,created_at_utc) values('de000000-0000-4000-0002-000000000005','de000000-0000-4000-0001-000000000002','DEMO - Conference photography','Harbour Conference','demo-client-5@example.invalid','Manual',false,now()-interval '5 days');""",
        """insert into studio_messages(id,conversation_id,author_display_name,visibility,body,created_at_utc) values('de000000-0000-4000-0004-000000000005','de000000-0000-4000-0002-000000000005','Harbour Conference','External','Fictional demo enquiry: Conference photography. Please confirm availability, package options and delivery dates.',now()-interval '5 days');""",
        """insert into studio_jobs(id,workspace_id,conversation_id,title,client_name,status,event_date,created_at_utc,assigned_to,job_lead) values('de000000-0000-4000-0003-000000000005','de000000-0000-4000-0001-000000000002','de000000-0000-4000-0002-000000000005','DEMO - Conference photography','Harbour Conference','Production',current_date+1,now()-interval '5 days',$1,$1);""",
        """update studio_conversations set job_id='de000000-0000-4000-0003-000000000005' where id='de000000-0000-4000-0002-000000000005';""",
        """insert into studio_financial_events(id,job_id,kind,amount,note,created_at_utc) values('de000000-0000-4000-0005-000000000005','de000000-0000-4000-0003-000000000005','Deposit',12500,'DEMO ONLY - fictional deposit, no payment processed',now());""",
        """insert into studio_schedule_entries(id,job_id,title,starts_at_utc,ends_at_utc,location,status,notes,created_at_utc) values('de000000-0000-4000-0006-000000000005','de000000-0000-4000-0003-000000000005','DEMO - Conference photography',date_trunc('day',now())+interval '1 days 15 hours',date_trunc('day',now())+interval '1 days 17 hours','Demo studio','Scheduled','Fictional demo appointment',now());""",
        """insert into studio_job_activities(id,job_id,activity_type,title,description,status,priority,assigned_to,due_at_utc,started_at_utc,completed_at_utc,created_by,created_at_utc,updated_at_utc) values('de000000-0000-4000-0007-000000000005','de000000-0000-4000-0003-000000000005','Other','DEMO - Edit and prepare delivery','Select images, retouch and prepare client delivery.','InProgress','Normal',$1,now()+interval '1 days',now(),null,$1,now(),now());""",
        """insert into studio_conversations(id,workspace_id,subject,contact_name,contact_address,channel,is_closed,created_at_utc) values('de000000-0000-4000-0002-000000000006','de000000-0000-4000-0001-000000000002','DEMO - Anniversary album enquiry','Reid Anniversary','demo-client-6@example.invalid','Manual',false,now()-interval '6 days');""",
        """insert into studio_messages(id,conversation_id,author_display_name,visibility,body,created_at_utc) values('de000000-0000-4000-0004-000000000006','de000000-0000-4000-0002-000000000006','Reid Anniversary','External','Fictional demo enquiry: Anniversary album enquiry. Please confirm availability, package options and delivery dates.',now()-interval '6 days');""",
        """insert into studio_conversations(id,workspace_id,subject,contact_name,contact_address,channel,is_closed,created_at_utc) values('de000000-0000-4000-0002-000000000007','de000000-0000-4000-0001-000000000003','DEMO - School photo day','Hillview School','demo-client-7@example.invalid','Manual',false,now()-interval '7 days');""",
        """insert into studio_messages(id,conversation_id,author_display_name,visibility,body,created_at_utc) values('de000000-0000-4000-0004-000000000007','de000000-0000-4000-0002-000000000007','Hillview School','External','Fictional demo enquiry: School photo day. Please confirm availability, package options and delivery dates.',now()-interval '7 days');""",
        """insert into studio_jobs(id,workspace_id,conversation_id,title,client_name,status,event_date,created_at_utc,assigned_to,job_lead) values('de000000-0000-4000-0003-000000000007','de000000-0000-4000-0001-000000000003','de000000-0000-4000-0002-000000000007','DEMO - School photo day','Hillview School','Ready for Client',current_date+3,now()-interval '7 days',$1,$1);""",
        """update studio_conversations set job_id='de000000-0000-4000-0003-000000000007' where id='de000000-0000-4000-0002-000000000007';""",
        """insert into studio_financial_events(id,job_id,kind,amount,note,created_at_utc) values('de000000-0000-4000-0005-000000000007','de000000-0000-4000-0003-000000000007','Deposit',17500,'DEMO ONLY - fictional deposit, no payment processed',now());""",
        """insert into studio_schedule_entries(id,job_id,title,starts_at_utc,ends_at_utc,location,status,notes,created_at_utc) values('de000000-0000-4000-0006-000000000007','de000000-0000-4000-0003-000000000007','DEMO - School photo day',date_trunc('day',now())+interval '3 days 15 hours',date_trunc('day',now())+interval '3 days 17 hours','Demo studio','Scheduled','Fictional demo appointment',now());""",
        """insert into studio_job_activities(id,job_id,activity_type,title,description,status,priority,assigned_to,due_at_utc,started_at_utc,completed_at_utc,created_by,created_at_utc,updated_at_utc) values('de000000-0000-4000-0007-000000000007','de000000-0000-4000-0003-000000000007','Other','DEMO - Edit and prepare delivery','Select images, retouch and prepare client delivery.','NotStarted','Normal',$1,now()+interval '3 days',null,null,$1,now(),now());""",
        """insert into studio_conversations(id,workspace_id,subject,contact_name,contact_address,channel,is_closed,created_at_utc) values('de000000-0000-4000-0002-000000000008','de000000-0000-4000-0001-000000000003','DEMO - Corporate headshots','Cedar Team','demo-client-8@example.invalid','Manual',false,now()-interval '8 days');""",
        """insert into studio_messages(id,conversation_id,author_display_name,visibility,body,created_at_utc) values('de000000-0000-4000-0004-000000000008','de000000-0000-4000-0002-000000000008','Cedar Team','External','Fictional demo enquiry: Corporate headshots. Please confirm availability, package options and delivery dates.',now()-interval '8 days');""",
        """insert into studio_jobs(id,workspace_id,conversation_id,title,client_name,status,event_date,created_at_utc,assigned_to,job_lead) values('de000000-0000-4000-0003-000000000008','de000000-0000-4000-0001-000000000003','de000000-0000-4000-0002-000000000008','DEMO - Corporate headshots','Cedar Team','Completed',current_date+4,now()-interval '8 days',$1,$1);""",
        """update studio_conversations set job_id='de000000-0000-4000-0003-000000000008' where id='de000000-0000-4000-0002-000000000008';""",
        """insert into studio_financial_events(id,job_id,kind,amount,note,created_at_utc) values('de000000-0000-4000-0005-000000000008','de000000-0000-4000-0003-000000000008','Deposit',20000,'DEMO ONLY - fictional deposit, no payment processed',now());""",
        """insert into studio_schedule_entries(id,job_id,title,starts_at_utc,ends_at_utc,location,status,notes,created_at_utc) values('de000000-0000-4000-0006-000000000008','de000000-0000-4000-0003-000000000008','DEMO - Corporate headshots',date_trunc('day',now())+interval '4 days 15 hours',date_trunc('day',now())+interval '4 days 17 hours','Demo studio','Scheduled','Fictional demo appointment',now());""",
        """insert into studio_job_activities(id,job_id,activity_type,title,description,status,priority,assigned_to,due_at_utc,started_at_utc,completed_at_utc,created_by,created_at_utc,updated_at_utc) values('de000000-0000-4000-0007-000000000008','de000000-0000-4000-0003-000000000008','Other','DEMO - Edit and prepare delivery','Select images, retouch and prepare client delivery.','Completed','Normal',$1,now()+interval '4 days',now(),now(),$1,now(),now());""",
        """insert into studio_conversations(id,workspace_id,subject,contact_name,contact_address,channel,is_closed,created_at_utc) values('de000000-0000-4000-0002-000000000009','de000000-0000-4000-0001-000000000003','DEMO - Product photography enquiry','Kingston Product Studio','demo-client-9@example.invalid','Manual',false,now()-interval '9 days');""",
        """insert into studio_messages(id,conversation_id,author_display_name,visibility,body,created_at_utc) values('de000000-0000-4000-0004-000000000009','de000000-0000-4000-0002-000000000009','Kingston Product Studio','External','Fictional demo enquiry: Product photography enquiry. Please confirm availability, package options and delivery dates.',now()-interval '9 days');""",
        """insert into studio_state(key,value,updated_at_utc) values('demo-data-v1',$1,now());"""
    ];
}

