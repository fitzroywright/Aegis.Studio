using Aegis.Studio.Application.EmailRouting;
using Aegis.Studio.Web.Notifications;

namespace Aegis.Studio.Web.Messaging;

public static class StudioMessageNotificationContext
{
    public static void PublishInbound(
        NotificationService notifications,
        InboundEmail email,
        EmailRouteResult route,
        Guid conversationId,
        Guid? personThreadId = null)
    {
        string sender = SenderLabel(email);
        string subject = BuildInboundSubject(email, route);
        string body = string.IsNullOrWhiteSpace(email.FromName) || string.Equals(email.FromName.Trim(), email.FromAddress.Trim(), StringComparison.OrdinalIgnoreCase)
            ? email.Subject
            : $"{email.Subject} · {email.FromAddress.Trim()}";

        if (route.DestinationType.Equals("Person", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(route.PersonUserName))
        {
            notifications.PublishToUser(
                route.PersonUserName!,
                "WorkspaceMessage",
                subject,
                body,
                personThreadId.HasValue ? "MessageThread" : "Conversation",
                (personThreadId ?? conversationId).ToString("D"));
            return;
        }

        if (route.DestinationType.Equals("Job", StringComparison.OrdinalIgnoreCase) &&
            route.DestinationId is Guid jobId)
        {
            notifications.PublishToAll("WorkspaceMessage", subject, body, "Job", jobId.ToString("D"));
            return;
        }

        if (route.WorkspaceId is Guid workspaceId)
        {
            notifications.PublishToAll("WorkspaceMessage", subject, body, "Workspace", workspaceId.ToString("D"));
            return;
        }

        notifications.PublishToAll("WorkspaceMessage", $"Aegis Studio — New message from {sender}", body);
    }

    public static string BuildInboundSubject(InboundEmail email, EmailRouteResult route)
    {
        string context = string.IsNullOrWhiteSpace(route.UserFacingContext) ? "Aegis Studio" : route.UserFacingContext;
        return $"{context} — New message from {SenderLabel(email)}";
    }

    public static string SenderLabel(InboundEmail email)
    {
        if (!string.IsNullOrWhiteSpace(email.FromName))
            return email.FromName.Trim();
        if (!string.IsNullOrWhiteSpace(email.FromAddress))
            return email.FromAddress.Trim();
        return "External sender";
    }
}
