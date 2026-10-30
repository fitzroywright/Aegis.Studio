using Npgsql;

namespace Aegis.Studio.Web.Operations;

// Presentation-focused demo upgrade. Only deterministic DEMO records are changed.
internal static class DemoPresentationData
{
    internal static bool Seed(NpgsqlConnection connection, NpgsqlTransaction transaction, string login)
    {
        using (var check = new NpgsqlCommand("select exists(select 1 from studio_state where key='demo-data-v4-visual-demo')", connection, transaction))
            if ((bool)check.ExecuteScalar()!) return false;

        Guid workspace = DemoDataExpansion.Id(1, 2);
        Guid conversation = DemoDataExpansion.Id(2, 4);
        Guid job = DemoDataExpansion.Id(3, 4);

        Execute(connection, transaction,
            "update studio_workspaces set name='DEMO - Weddings and Events', updated_at_utc=now() where id=$1", workspace);
        Execute(connection, transaction,
            "update studio_conversations set subject='Carter Wedding', contact_name='Emily Carter', contact_address='emily.carter@example.invalid' where id=$1", conversation);
        Execute(connection, transaction,
            "update studio_jobs set title='Carter Wedding', client_name='Emily & Daniel Carter', status='Production', event_date=current_date where id=$1", job);
        Execute(connection, transaction,
            "update studio_schedule_entries set title='Carter Wedding', location='Hope Gardens, Kingston', status='Completed', notes='DEMO — wedding photography session completed' where job_id=$1", job);

        Execute(connection, transaction,
            "update studio_messages set author_display_name='Emily Carter', visibility='External', body='Here are a few inspiration photos we like for the style. We are excited!' where id=$1",
            DemoDataExpansion.Id(4, 4));

        InsertMessage(connection, transaction, DemoDataExpansion.Id(30, 401), conversation, "Jordan Diaz", "Internal",
            "Great! I’ve added these to the planning notes. We’ll follow this style on the shoot.", "5 days");
        InsertMessage(connection, transaction, DemoDataExpansion.Id(30, 402), conversation, "Emily Carter", "External",
            "The venue confirmed the timing. Guests arrive at 3pm. Ceremony at 4pm.", "4 days");
        InsertMessage(connection, transaction, DemoDataExpansion.Id(30, 403), conversation, "Taylor Kim", "Internal",
            "Final shot list attached for the day. Family groups are confirmed.", "3 days");
        InsertMessage(connection, transaction, DemoDataExpansion.Id(30, 404), conversation, "Emily Carter", "External",
            "Thank you. Please include a few detail shots of the rings, flowers and reception tables.", "2 days");
        InsertMessage(connection, transaction, DemoDataExpansion.Id(30, 405), conversation, "Jordan Diaz", "Internal",
            "Shoot complete. Media is being uploaded and organized for review.", "1 day");

        // Make the existing demo tasks read like the approved production progression.
        string[] titles = ["Shoot Completed", "Upload Media", "Review & Select", "Process & Edit"];
        string[] statuses = ["Completed", "InProgress", "NotStarted", "NotStarted"];
        for (int i = 0; i < 4; i++)
        {
            Guid activity = DemoDataExpansion.Id(22, 40 + i);
            Execute(connection, transaction,
                "update studio_job_activities set title=$1, description=$2, status=$3, priority='Normal', assigned_to=$4, updated_at_utc=now() where id=$5",
                titles[i],
                i == 0 ? "Wedding photography session completed."
                    : i == 1 ? "Finish upload and organize media."
                    : i == 2 ? "Client review and selection."
                    : "Editing and retouching.",
                statuses[i], login, activity);
        }

        AddActivity(connection, transaction, DemoDataExpansion.Id(31, 401), job, "Client Approval",
            "Finalize images for gallery.", "NotStarted", login, 3);
        AddActivity(connection, transaction, DemoDataExpansion.Id(31, 402), job, "Publish to Gallery",
            "Publish approved photographs to the client gallery.", "NotStarted", login, 4);

        // Remove the older placeholder-only subject rows. DemoDataExpansion now
        // supplies the named wedding people/groups and links the 50 photos to them.
        Execute(connection, transaction,
            "delete from studio_job_subjects where id in ($1,$2,$3,$4,$5)",
            DemoDataExpansion.Id(32, 400),
            DemoDataExpansion.Id(32, 401),
            DemoDataExpansion.Id(32, 402),
            DemoDataExpansion.Id(32, 403),
            DemoDataExpansion.Id(32, 404));

        Execute(connection, transaction,
            "insert into studio_workspace_announcements(id,workspace_id,title,body,created_by) values($1,$2,'Carter Wedding demo','Open Carter Wedding to explore its Session Plan, linked local photographs, production work and job details.',$3) on conflict (id) do update set body=excluded.body",
            DemoDataExpansion.Id(33, 4), workspace, login);

        Execute(connection, transaction,
            "insert into studio_state(key,value,updated_at_utc) values('demo-data-v4-visual-demo',$1,now()) on conflict(key) do update set value=excluded.value,updated_at_utc=excluded.updated_at_utc", login);
        return true;
    }

    private static void InsertMessage(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, Guid conversation, string author, string visibility, string body, string age)
    {
        Execute(connection, transaction,
            $"insert into studio_messages(id,conversation_id,author_display_name,visibility,body,created_at_utc) values($1,$2,$3,$4,$5,now()-interval '{age}') on conflict (id) do nothing",
            id, conversation, author, visibility, body);
    }

    private static void AddActivity(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, Guid job, string title, string description, string status, string login, int dueDays)
    {
        Execute(connection, transaction,
            "insert into studio_job_activities(id,job_id,activity_type,title,description,status,priority,assigned_to,due_at_utc,created_by,created_at_utc,updated_at_utc) values($1,$2,'Other',$3,$4,$5,'Normal',$6,now()+($7 || ' days')::interval,$6,now(),now()) on conflict (id) do nothing",
            id, job, title, description, status, login, dueDays.ToString());
    }

    private static void Execute(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, params object[] values)
    {
        using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (object value in values) command.Parameters.AddWithValue(value);
        command.ExecuteNonQuery();
    }
}
