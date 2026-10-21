using Aegis.Studio.Web.Email;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Notifications;

public sealed class JobEventNotifier
{
    private readonly StudioDatabase database;
    private readonly NotificationService notifications;
    private readonly StudioStore studio;
    private readonly StudioRecipientResolver recipients;
    private readonly OutboundEmailStore outbox;

    public JobEventNotifier(
        StudioDatabase database,
        NotificationService notifications,
        StudioStore studio,
        StudioRecipientResolver recipients,
        OutboundEmailStore outbox)
    {
        this.database = database;
        this.notifications = notifications;
        this.studio = studio;
        this.recipients = recipients;
        this.outbox = outbox;
        EnsureSchema();
    }

    public void Publish(Guid jobId, string eventType, string subject, string body, bool requesterEligible = false)
    {
        StudioJob? job = studio.GetJob(jobId);
        if (job is null)
        {
            return;
        }

        foreach (string userName in recipients.ResolveForJob(jobId))
        {
            notifications.PublishToUser(userName, eventType, subject, body, "Job", jobId.ToString("D"));
        }

        if (requesterEligible && RequesterWants(jobId, eventType))
        {
            StudioConversation? conversation = studio.GetConversation(job.ConversationId);
            if (conversation is not null && conversation.Channel.Equals("Email", StringComparison.OrdinalIgnoreCase) && conversation.ContactAddress.Contains('@')) outbox.QueueNotification(conversation, subject, body);
        }

        WorkflowAutomationRuntime.Publish(eventType, jobId, new { subject, body, requesterEligible });
    }

    public RequesterNotificationPreferences GetRequesterPreferences(Guid jobId)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select schedule_changes,status_changes,production_milestones,delivery_updates,booking_received,booking_confirmed,deposit_required,booking_declined_or_changed,ready_for_delivery from studio_requester_notification_preferences where job_id=$1";
        command.Parameters.AddWithValue(jobId);
        using NpgsqlDataReader reader = command.ExecuteReader();
        return reader.Read() ? new RequesterNotificationPreferences(reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5), reader.GetBoolean(6), reader.GetBoolean(7), reader.GetBoolean(8)) : new RequesterNotificationPreferences(false, false, false, false);
    }

    public RequesterNotificationPreferences SaveRequesterPreferences(Guid jobId, RequesterNotificationPreferences preferences)
    {
        if (studio.GetJob(jobId) is null) throw new KeyNotFoundException("Job was not found.");
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            insert into studio_requester_notification_preferences(job_id,schedule_changes,status_changes,production_milestones,delivery_updates,booking_received,booking_confirmed,deposit_required,booking_declined_or_changed,ready_for_delivery,updated_at_utc)
            values($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,now())
            on conflict(job_id) do update set schedule_changes=excluded.schedule_changes,status_changes=excluded.status_changes,
                production_milestones=excluded.production_milestones,delivery_updates=excluded.delivery_updates,booking_received=excluded.booking_received,booking_confirmed=excluded.booking_confirmed,deposit_required=excluded.deposit_required,
                booking_declined_or_changed=excluded.booking_declined_or_changed,ready_for_delivery=excluded.ready_for_delivery,updated_at_utc=now()
            """;
        command.Parameters.AddWithValue(jobId); command.Parameters.AddWithValue(preferences.ScheduleChanges); command.Parameters.AddWithValue(preferences.StatusChanges); command.Parameters.AddWithValue(preferences.ProductionMilestones); command.Parameters.AddWithValue(preferences.DeliveryUpdates); command.ExecuteNonQuery();
        return preferences;
    }

    private bool RequesterWants(Guid jobId, string eventType)
    {
        RequesterNotificationPreferences preferences = GetRequesterPreferences(jobId);
        return eventType switch { "ScheduleChanged" => preferences.ScheduleChanges, "JobStatusChanged" => preferences.StatusChanges, "ProductionChanged" => preferences.ProductionMilestones, "DeliveryChanged" => preferences.DeliveryUpdates, "BookingReceived" => preferences.BookingReceived, "BookingConfirmed" => preferences.BookingConfirmed, "DepositRequired" => preferences.DepositRequired, "BookingDeclinedOrChanged" => preferences.BookingDeclinedOrChanged, "ReadyForDelivery" => preferences.ReadyForDelivery, _ => false };
    }

    private void EnsureSchema()
    {
        using NpgsqlConnection connection = database.OpenConnection(); using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            create table if not exists studio_requester_notification_preferences
            (
                job_id uuid primary key references studio_jobs(id) on delete cascade,
                schedule_changes boolean not null default false,
                status_changes boolean not null default false,
                production_milestones boolean not null default false,
                delivery_updates boolean not null default false,
                booking_received boolean not null default false,
                booking_confirmed boolean not null default false,
                deposit_required boolean not null default false,
                booking_declined_or_changed boolean not null default false,
                ready_for_delivery boolean not null default false,
                updated_at_utc timestamptz not null
            );
            alter table studio_requester_notification_preferences add column if not exists booking_received boolean not null default false;
            alter table studio_requester_notification_preferences add column if not exists booking_confirmed boolean not null default false;
            alter table studio_requester_notification_preferences add column if not exists deposit_required boolean not null default false;
            alter table studio_requester_notification_preferences add column if not exists booking_declined_or_changed boolean not null default false;
            alter table studio_requester_notification_preferences add column if not exists ready_for_delivery boolean not null default false;
            """; command.ExecuteNonQuery();
    }
}

public sealed record RequesterNotificationPreferences(bool ScheduleChanges,bool StatusChanges,bool ProductionMilestones,bool DeliveryUpdates,bool BookingReceived=false,bool BookingConfirmed=false,bool DepositRequired=false,bool BookingDeclinedOrChanged=false,bool ReadyForDelivery=false);
