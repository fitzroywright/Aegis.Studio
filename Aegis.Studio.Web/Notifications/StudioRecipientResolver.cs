using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Notifications;

public sealed class StudioRecipientResolver
{
    private readonly StudioDatabase database;
    private readonly StudioStore studio;
    private readonly WorkspaceAccessService workspaceAccess;
    private readonly JobAccessService jobAccess;

    public StudioRecipientResolver(
        StudioDatabase database,
        StudioStore studio,
        WorkspaceAccessService workspaceAccess,
        JobAccessService jobAccess)
    {
        this.database = database;
        this.studio = studio;
        this.workspaceAccess = workspaceAccess;
        this.jobAccess = jobAccess;
    }

    public IReadOnlyList<string>? Resolve(string? referenceType, string? referenceId)
    {
        if (string.IsNullOrWhiteSpace(referenceType) || string.IsNullOrWhiteSpace(referenceId))
        {
            return null;
        }

        HashSet<string> recipients = new(StringComparer.OrdinalIgnoreCase);

        if (referenceType.Equals("Job", StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParse(referenceId, out Guid jobId))
        {
            AddJobRecipients(jobId, recipients);
            return recipients.ToArray();
        }

        if (referenceType.Equals("Conversation", StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParse(referenceId, out Guid conversationId))
        {
            StudioConversation? conversation = studio.GetConversation(conversationId);
            if (conversation is null)
            {
                return [];
            }

            if (conversation.JobId.HasValue)
            {
                AddJobRecipients(conversation.JobId.Value, recipients);
            }
            else
            {
                AddWorkspaceRecipients(conversation.WorkspaceId, recipients);
            }

            return recipients.ToArray();
        }

        if (referenceType.Equals("Workspace", StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParse(referenceId, out Guid workspaceId))
        {
            AddWorkspaceRecipients(workspaceId, recipients);
            return recipients.ToArray();
        }

        if (referenceType.Equals("Booking", StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParse(referenceId, out Guid bookingId))
        {
            using NpgsqlConnection connection = database.OpenConnection();
            using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText = "select job_id from studio_bookings where id=$1";
            command.Parameters.AddWithValue(bookingId);

            if (command.ExecuteScalar() is Guid bookingJobId)
            {
                AddJobRecipients(bookingJobId, recipients);
            }

            return recipients.ToArray();
        }

        return null;
    }

    public IReadOnlyList<string> ResolveForJob(Guid jobId)
    {
        HashSet<string> recipients = new(StringComparer.OrdinalIgnoreCase);
        AddJobRecipients(jobId, recipients);
        return recipients.ToArray();
    }

    private void AddJobRecipients(Guid jobId, HashSet<string> recipients)
    {
        StudioJob? job = studio.GetJob(jobId);
        if (job is null)
        {
            return;
        }

        foreach (JobMember member in jobAccess.GetMembers(jobId).Where(x => x.IsActive))
        {
            recipients.Add(member.UserName);
        }

        AddWorkspaceRecipients(job.WorkspaceId, recipients);
    }

    private void AddWorkspaceRecipients(Guid workspaceId, HashSet<string> recipients)
    {
        foreach (WorkspaceMember member in workspaceAccess.GetMembers(workspaceId).Where(x => x.IsActive))
        {
            recipients.Add(member.UserName);
        }
    }
}
