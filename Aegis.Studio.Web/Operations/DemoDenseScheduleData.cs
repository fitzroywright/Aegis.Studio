using Npgsql;

namespace Aegis.Studio.Web.Operations;

// Fast, database-only demo workload. It is deliberately separate from photo seeding so
// useful operational data can be committed in small batches even when remote media is slow.
internal static class DemoDenseScheduleData
{
    internal const int Days = 21;
    internal const int JobsPerDay = 6;
    internal const int BatchDays = 3;
    internal static int BatchCount => (Days + BatchDays - 1) / BatchDays;

    private static readonly string[] WorkspaceNames =
    [
        "Portraits and IDs",
        "Weddings and Events",
        "Schools and Corporate",
        "Graduations",
        "Commercial and Products",
        "Sports and Groups"
    ];

    private static readonly (string Name,string Email,string Phone)[] Clients =
    [
        ("Miller Family","miller.family@example.invalid","876-555-0201"),
        ("Campion College","campion@example.invalid","876-555-0202"),
        ("Carter Corporation","carter.corp@example.invalid","876-555-0203"),
        ("Emily & Daniel Carter","carter.wedding@example.invalid","876-555-0204"),
        ("Harrison Family","harrison.family@example.invalid","876-555-0205"),
        ("Peterson Family","peterson.family@example.invalid","876-555-0206"),
        ("Wilson Family","wilson.family@example.invalid","876-555-0207"),
        ("Thompson Family","thompson.family@example.invalid","876-555-0208"),
        ("North Coast Academy","northcoast.academy@example.invalid","876-555-0209"),
        ("Kingston Medical Group","kmg@example.invalid","876-555-0210"),
        ("Harbour Conference Ltd","harbour.events@example.invalid","876-555-0211"),
        ("Blue Mountain Products","blue.mountain@example.invalid","876-555-0212"),
        ("Anderson Family","anderson.family@example.invalid","876-555-0213"),
        ("St Andrew Prep","standrew.prep@example.invalid","876-555-0214"),
        ("Island Finance Group","island.finance@example.invalid","876-555-0215"),
        ("Kingston Dance Academy","dance.academy@example.invalid","876-555-0216"),
        ("Marley & Reid","marley.reid@example.invalid","876-555-0217"),
        ("Portmore United Youth","portmore.united@example.invalid","876-555-0218"),
        ("Hope Medical Centre","hope.medical@example.invalid","876-555-0219"),
        ("Jamaica Legal Partners","jlp@example.invalid","876-555-0220"),
        ("Blue Reef Resorts","blue.reef@example.invalid","876-555-0221"),
        ("Liguanea Montessori","liguanea.montessori@example.invalid","876-555-0222"),
        ("Kingston Creative Co","kingston.creative@example.invalid","876-555-0223"),
        ("Brown Family","brown.family@example.invalid","876-555-0224")
    ];

    private static readonly string[] JobStatuses =
    [
        "Needs Scheduling","Scheduled","Photography","Awaiting Upload","Culling","Editing",
        "Retouching","Client Review","QC","Printing","Ready for Delivery","Completed"
    ];

    private static readonly string[] TaskTypes =
    [
        "Other","Other","FileIngest","Cull","Edit","Retouch","QC","Print","Delivery"
    ];

    private static readonly string[] TaskOwners =
    [
        "demo.frontdesk","demo.scheduler","demo.ingest","demo.culling","demo.edit",
        "demo.retouch","demo.qc","demo.production","demo.delivery"
    ];

    private static readonly string[] TaskTitles =
    [
        "Confirm client and job brief",
        "Confirm schedule, location and crew",
        "Ingest and verify files",
        "Cull and organize selects",
        "Edit selected images",
        "Retouch approved images",
        "Quality-control review",
        "Prepare production / print output",
        "Prepare delivery and client handoff"
    ];

    internal static int SeedBatch(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string login,
        int batchIndex,
        Action<DemoSeedProgress>? report = null)
    {
        if (batchIndex < 0 || batchIndex >= BatchCount)
            throw new ArgumentOutOfRangeException(nameof(batchIndex));

        Execute(connection,transaction,"select set_config('studio.demo_seed','true',true)");
        EnsureReferenceData(connection, transaction, login);

        int firstDay = batchIndex * BatchDays;
        int lastDayExclusive = Math.Min(Days, firstDay + BatchDays);
        int seeded = 0;
        int total = (lastDayExclusive - firstDay) * JobsPerDay;

        for (int day = firstDay; day < lastDayExclusive; day++)
        {
            for (int workspace = 1; workspace <= JobsPerDay; workspace++)
            {
                SeedJob(connection, transaction, login, day, workspace);
                seeded++;
                int pct = 2 + (int)(23.0 * (batchIndex * BatchDays * JobsPerDay + seeded) / (Days * JobsPerDay));
                report?.Invoke(new(
                    Math.Min(25, pct),
                    "Operational demo batches",
                    seeded,
                    total,
                    $"Batch {batchIndex + 1}/{BatchCount}: day {day + 1}/{Days}, workspace {workspace}/{JobsPerDay}."));
            }
        }

        Execute(connection, transaction,
            "insert into studio_state(key,value,updated_at_utc) values($1,$2,now()) on conflict(key) do update set value=excluded.value,updated_at_utc=excluded.updated_at_utc",
            $"demo-data-dense-v1-batch-{batchIndex + 1}", login);

        return seeded;
    }

    private static void EnsureReferenceData(NpgsqlConnection c, NpgsqlTransaction tx, string login)
    {
        for (int workspace = 1; workspace <= WorkspaceNames.Length; workspace++)
        {
            Guid workspaceId = ExistingWorkspaceId(workspace);
            Execute(c, tx,
                "insert into studio_workspaces(id,name,email_address,route_key,is_enabled,sort_order,created_at_utc,updated_at_utc) values($1,$2,null,$3,true,$4,now(),now()) on conflict(id) do update set name=excluded.name,is_enabled=true,sort_order=excluded.sort_order,updated_at_utc=now()",
                workspaceId, WorkspaceNames[workspace - 1], $"studio-demo-full-{workspace}", 100 + workspace);
            Execute(c, tx,
                "insert into studio_workspace_members(workspace_id,user_name,display_name,is_active,access_workspace,can_view,reply_requester,view_attachments,add_attachments,create_job,edit_job,manage_schedule,manage_production,notify_members,edit_workspace,manage_members,admin_workspace,send_as_self,created_at_utc,updated_at_utc) values($1,$2,$2,true,true,true,true,true,true,true,true,true,true,true,true,true,true,false,now(),now()) on conflict(workspace_id,user_name) do update set display_name=excluded.display_name,is_active=true,access_workspace=true,can_view=true,create_job=true,edit_job=true,manage_schedule=true,manage_production=true,updated_at_utc=now()",
                workspaceId, login);
        }

        for (int i = 0; i < Clients.Length; i++)
        {
            var client = Clients[i];
            Execute(c, tx,
                "insert into studio_clients(id,display_name,email,phone,updated_at_utc) values($1,$2,$3,$4,now()) on conflict(id) do update set display_name=excluded.display_name,email=excluded.email,phone=excluded.phone,updated_at_utc=now()",
                Id(92, i + 1), client.Name, client.Email, client.Phone);
        }
    }

    private static void SeedJob(NpgsqlConnection c, NpgsqlTransaction tx, string login, int day, int workspace)
    {
        int sequence = day * JobsPerDay + workspace;
        int clientIndex = (day * 7 + workspace * 3) % Clients.Length;
        var client = Clients[clientIndex];
        string type = JobType(workspace, day);
        string title = $"{client.Name} - {type}";
        string status = JobStatuses[(day + workspace * 2) % JobStatuses.Length];

        Guid jobId = Id(93, sequence);
        Guid conversationId = Id(94, sequence);
        Guid clientId = Id(92, clientIndex + 1);
        Guid workspaceId = ExistingWorkspaceId(workspace);

        int startHour = 8 + ((workspace - 1) * 2);
        DateTimeOffset start = new(DateTime.UtcNow.Date.AddDays(day).AddHours(startHour), TimeSpan.Zero);
        DateTimeOffset end = start.AddHours(workspace == 2 ? 5 : workspace is 3 or 4 ? 3 : 2);
        string location = Location(workspace, day);

        Execute(c, tx,
            "insert into studio_conversations(id,workspace_id,subject,contact_name,contact_address,channel,is_closed,created_at_utc) values($1,$2,$3,$4,$5,$6,false,now()) on conflict(id) do update set subject=excluded.subject,contact_name=excluded.contact_name,contact_address=excluded.contact_address,channel=excluded.channel",
            conversationId, workspaceId, title, client.Name, client.Email, sequence % 4 == 0 ? "Phone" : sequence % 3 == 0 ? "Portal" : "Email");

        Execute(c, tx,
            "insert into studio_jobs(id,workspace_id,conversation_id,title,client_name,status,event_date,created_at_utc,assigned_to,job_lead,client_id) values($1,$2,$3,$4,$5,$6,$7,now(),$8,$8,$9) on conflict(id) do update set workspace_id=excluded.workspace_id,conversation_id=excluded.conversation_id,title=excluded.title,client_name=excluded.client_name,status=excluded.status,event_date=excluded.event_date,assigned_to=excluded.assigned_to,job_lead=excluded.job_lead,client_id=excluded.client_id",
            jobId, workspaceId, conversationId, title, client.Name, status, DateOnly.FromDateTime(start.UtcDateTime), Photographer(sequence), clientId);

        Execute(c, tx, "update studio_conversations set job_id=$2 where id=$1", conversationId, jobId);
        Execute(c, tx, "insert into studio_media_galleries(id,name,default_lens,default_collection) values($1,'Media','Auto','Unclassified') on conflict(id) do nothing", jobId);

        Execute(c, tx,
            "insert into studio_messages(id,conversation_id,author_display_name,visibility,body,created_at_utc) values($1,$2,$3,'External',$4,now()) on conflict(id) do update set body=excluded.body",
            Id(90, sequence), conversationId, client.Name, $"DEMO enquiry for {type}. Please confirm timing, deliverables and any preparation requirements.");

        Execute(c, tx,
            "insert into studio_schedule_entries(id,job_id,title,starts_at_utc,ends_at_utc,location,status,notes,created_at_utc) values($1,$2,$3,$4,$5,$6,'Scheduled','Dense demo schedule - fictional data',now()) on conflict(id) do update set title=excluded.title,starts_at_utc=excluded.starts_at_utc,ends_at_utc=excluded.ends_at_utc,location=excluded.location,status=excluded.status,notes=excluded.notes",
            Id(97, sequence), jobId, title, start, end, location);

        int subjects = SubjectCount(workspace, sequence);
        for (int subject = 0; subject < subjects; subject++)
        {
            string name = SubjectName(sequence, subject);
            Execute(c, tx,
                "insert into studio_job_subjects(id,job_id,gallery_id,name,reference,notes,updated_by,production_status,attributes,collection_name,scheduled_at_utc,schedule_location,contact_name,contact_email,contact_phone,photo_notes,is_system) values($1,$2,$2,$3,$4,'Dense DEMO subject','demo-seed',$5,'{}'::jsonb,$6,$7,$8,'','','','Demo capture notes',false) on conflict(id) do update set name=excluded.name,reference=excluded.reference,production_status=excluded.production_status,collection_name=excluded.collection_name,scheduled_at_utc=excluded.scheduled_at_utc,schedule_location=excluded.schedule_location,updated_at_utc=now()",
                SubjectId(sequence, subject), jobId, name, $"D{day + 1:00}-W{workspace}-S{subject + 1:00}",
                SubjectStatus(status, subject), Collection(workspace, subject), start.AddMinutes(subject * 5), location);
        }

        for (int task = 0; task < TaskTitles.Length; task++)
        {
            string taskStatus = TaskStatus(status, task, sequence);
            DateTimeOffset due = start.AddHours(task - 2);
            Execute(c, tx,
                "insert into studio_job_activities(id,job_id,activity_type,title,description,status,priority,assigned_to,scheduled_at_utc,due_at_utc,started_at_utc,completed_at_utc,location,blocked_reason,handoff_to,created_by,created_at_utc,updated_at_utc) values($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,'demo-seed',now(),now()) on conflict(id) do update set activity_type=excluded.activity_type,title=excluded.title,description=excluded.description,status=excluded.status,priority=excluded.priority,assigned_to=excluded.assigned_to,scheduled_at_utc=excluded.scheduled_at_utc,due_at_utc=excluded.due_at_utc,started_at_utc=excluded.started_at_utc,completed_at_utc=excluded.completed_at_utc,location=excluded.location,blocked_reason=excluded.blocked_reason,handoff_to=excluded.handoff_to,updated_at_utc=now()",
                TaskId(sequence, task), jobId, TaskTypes[task], $"{TaskTitles[task]} - {title}",
                $"DEMO {TeamName(task)} work for {type}.", taskStatus,
                taskStatus is "Blocked" or "ReworkRequired" ? "High" : task % 3 == 0 ? "Normal" : "Low",
                task == 1 ? login : TaskOwners[task], start.AddHours(task - 4), due,
                taskStatus == "InProgress" ? DateTimeOffset.UtcNow : DBNull.Value,
                taskStatus == "Completed" ? DateTimeOffset.UtcNow.AddHours(-1) : DBNull.Value,
                location,
                taskStatus == "Blocked" ? "Waiting on client or upstream handoff" : DBNull.Value,
                task < TaskTitles.Length - 1 ? TaskOwners[task + 1] : DBNull.Value);

            Execute(c, tx, "update studio_job_activities set collection_name=$2 where id=$1",
                TaskId(sequence, task), Collection(workspace, task));
        }

        decimal charge = 18000m + workspace * 12000m + (sequence % 5) * 3500m;
        Execute(c, tx,
            "insert into studio_financial_events(id,job_id,kind,amount,note,created_at_utc) values($1,$2,'Charge',$3,'DEMO scheduled job charge',now()) on conflict(id) do update set amount=excluded.amount,note=excluded.note",
            Id(98, sequence), jobId, charge);
        if (sequence % 3 != 0)
            Execute(c, tx,
                "insert into studio_financial_events(id,job_id,kind,amount,note,created_at_utc) values($1,$2,'Deposit',$3,'DEMO deposit',now()) on conflict(id) do update set amount=excluded.amount,note=excluded.note",
                Id(99, sequence), jobId, charge / 2m);
    }

    private static Guid ExistingWorkspaceId(int workspace) =>
        Guid.Parse($"de000000-0000-4000-0001-{workspace:000000000000}");

    private static Guid Id(int group, int number) =>
        Guid.Parse($"de100000-0000-4000-{group:0000}-{number:000000000000}");

    private static Guid SubjectId(int sequence, int subject) =>
        Guid.Parse($"de110000-0000-4000-{sequence:0000}-{subject + 1:000000000000}");

    private static Guid TaskId(int sequence, int task) =>
        Guid.Parse($"de120000-0000-4000-{sequence:0000}-{task + 1:000000000000}");

    private static string Photographer(int sequence) =>
        new[] { "demo.sam", "demo.jason", "demo.anna", "demo.michael" }[sequence % 4];

    private static int SubjectCount(int workspace, int sequence) => workspace switch
    {
        1 => 4 + sequence % 3,
        2 => 6 + sequence % 4,
        3 => 10 + sequence % 5,
        4 => 12 + sequence % 6,
        5 => 5 + sequence % 3,
        6 => 8 + sequence % 5,
        _ => 4
    };

    private static string JobType(int workspace, int day) => workspace switch
    {
        1 => new[] { "Family Portraits", "ID Photos", "Senior Portraits", "Passport Photos" }[day % 4],
        2 => new[] { "Wedding Coverage", "Engagement Session", "Corporate Event", "Birthday Event" }[day % 4],
        3 => new[] { "School Portrait Day", "Executive Headshots", "Staff ID Day", "Class Group Photos" }[day % 4],
        4 => new[] { "Graduation Portraits", "Graduation Ceremony", "Retake Day", "Valedictorian Portraits" }[day % 4],
        5 => new[] { "Product Catalogue", "Annual Report Portraits", "Campaign Photography", "Menu Product Shoot" }[day % 4],
        6 => new[] { "Football Team", "Track Portraits", "Swim Team", "Dance Group" }[day % 4],
        _ => "Studio Session"
    };

    private static string Location(int workspace, int day) => workspace switch
    {
        1 => day % 2 == 0 ? "Studio A" : "Studio B",
        2 => day % 2 == 0 ? "Hope Gardens" : "Jamaica Pegasus",
        3 => day % 2 == 0 ? "Client Campus" : "Studio B",
        4 => day % 2 == 0 ? "School Campus" : "Studio A",
        5 => day % 2 == 0 ? "Commercial Bay" : "Client Location",
        6 => day % 2 == 0 ? "National Stadium" : "National Aquatic Centre",
        _ => "Studio"
    };

    private static string Collection(int workspace, int index) => workspace switch
    {
        2 => new[] { "Getting Ready", "Ceremony", "Portraits", "Reception" }[index % 4],
        3 or 4 => new[] { "Roster", "Stage", "Groups", "Unclassified" }[index % 4],
        5 => new[] { "Products", "Details", "Selects", "Finals" }[index % 4],
        6 => new[] { "Individuals", "Team", "Action", "Groups" }[index % 4],
        _ => new[] { "Selects", "Proofs", "Finals", "Unclassified" }[index % 4]
    };

    private static string SubjectStatus(string jobStatus, int subject)
    {
        if (jobStatus == "Completed") return "Ready";
        if (jobStatus is "Printing" or "Ready for Delivery") return subject % 5 == 0 ? "Needs QC" : "Ready for Production";
        if (jobStatus is "Editing" or "Retouching" or "QC") return subject % 4 == 0 ? "Needs Retake" : "Needs Editing";
        return subject % 3 == 0 ? "Not Photographed" : "Scheduled";
    }

    private static string TaskStatus(string jobStatus, int task, int sequence)
    {
        int progress = Array.IndexOf(JobStatuses, jobStatus);
        int threshold = progress switch
        {
            <= 0 => 1,
            1 => 2,
            2 => 3,
            3 => 4,
            4 => 5,
            5 => 6,
            6 => 6,
            7 => 7,
            8 => 7,
            9 => 8,
            10 => 8,
            _ => 9
        };

        if (task < threshold) return "Completed";
        if (task == threshold && threshold < TaskTitles.Length)
            return sequence % 11 == 0 ? "Blocked" : sequence % 7 == 0 ? "ReworkRequired" : "InProgress";
        return "NotStarted";
    }

    private static string SubjectName(int sequence, int subject)
    {
        string[] given = ["Alicia","Jordan","Samantha","Jason","Amelia","Daniel","Emily","Michael","Anna","Ethan","Sarah","James","Chloe","Liam","Naomi","Noah","Grace","Joshua","Leah","Matthew"];
        string[] family = ["Brown","Williams","Clarke","Campbell","Thompson","Henry","Grant","Wright","Johnson","Robinson","Miller","Wilson","Reid","Walker","Smith","Lewis","Morgan","Carter","Peterson","Harrison"];
        return $"{given[(sequence + subject) % given.Length]} {family[(sequence * 3 + subject) % family.Length]}";
    }

    private static string TeamName(int task) => task switch
    {
        0 => "Front Desk",
        1 => "Scheduling",
        2 => "Ingest",
        3 => "Culling",
        4 => "Editing",
        5 => "Retouching",
        6 => "QC",
        7 => "Production",
        8 => "Delivery",
        _ => "Operations"
    };

    private static void Execute(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, params object[] values)
    {
        using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (object value in values) command.Parameters.AddWithValue(value);
        command.ExecuteNonQuery();
    }
}

