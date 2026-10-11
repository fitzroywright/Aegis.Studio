namespace Aegis.Studio.Web.Workflow;

public static class OwnerAssignmentRules
{
    public static bool CanRespond(string actor, string assignedTo, string status)
        => string.Equals(actor.Trim(), assignedTo.Trim(), StringComparison.OrdinalIgnoreCase) && status is "Assigned" or "Accepted";
}
