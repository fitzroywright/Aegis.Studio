using Aegis.Studio.Web.Production;

namespace Aegis.Studio.Web.Operations;

// A projection of existing facts. Selecting an action never changes workflow state.
public sealed record NextActionFacts(string Stage, bool HasClient, string? Owner, string? OwnerStatus,
    decimal DepositOutstanding, bool HasBookingRequest, int Subjects, int Unclassified,
    string? BlockedReason, string? CorrectionKind, GraphTask[] Tasks);
public sealed record NextActionChoice(string Key, string Title, string Reason, string Route,
    string? Selector = null, Guid? TaskId = null, string? AssignedTo = null, string? After = null);

public static class NextActionRules
{
    public static bool Closed(string stage) => stage is "Completed" or "Closed" or "Cancelled" or "Archived";
    public static NextActionChoice? Job(NextActionFacts f)
    {
        if (Closed(f.Stage)) return null;
        if (!string.IsNullOrWhiteSpace(f.BlockedReason)) return new("resolve-blocker", "Resolve job blocker", f.BlockedReason, "workflow", "[data-wf-block]");
        if (!f.HasClient) return new("match-client", "Assign a client", "This job is not linked to a client record.", "association");
        if (f.Stage is "Pending" or "Awaiting Deposit")
            return f.DepositOutstanding > 0
                ? new("collect-deposit", "Collect deposit", "The required deposit has not been fully recorded. Confirm payment or obtain an authorised override.", "deposit", After: "Confirm booking")
                : new("confirm-booking", "Confirm booking", "Review the booking requirements and confirmation conditions.", "workflow", "[data-wf-action=\"confirm\"]", After: "Assign job owner");
        if (f.OwnerStatus == "Assigned") return new("accept-owner", "Accept job ownership", "The assigned job owner has not accepted this job.", "workflow", "[data-wf-owner-response=\"accept-owner\"]", AssignedTo: f.Owner, After: "Complete Session Plan");
        if (string.IsNullOrWhiteSpace(f.Owner) || f.OwnerStatus is "Declined" or "Superseded" || f.Stage == "Needs Assignment")
            return new("assign-owner", "Assign job owner", "An accepted job owner is required to coordinate the work.", "workflow", "[data-wf-owner]", After: "Complete Session Plan");
        if (f.Subjects == 0 && f.Stage is "Confirmed" or "Needs Photographer Assignment" or "Awaiting Scheduling")
            return new("session-plan", "Complete Session Plan", "No subjects have been recorded for this job.", "session-plan", After: "Assign photographers and schedule sessions");
        if (f.Stage == "Needs Photographer Assignment") return new("assign-photographer", "Assign photographer", "A required photography session needs a photographer.", "workflow", "[data-wf-session]", After: "Schedule session");
        if (f.Stage == "Awaiting Scheduling") return new("schedule", "Schedule session", "Required photography sessions are not fully scheduled.", "workflow", "[data-wf-session]", After: "Start photography");
        if (f.Stage is "Scheduled" or "Photography In Progress") return new("shoot", f.Stage == "Scheduled" ? "Start photography" : "Complete photography", "Record the session and subject progress in the Session Plan.", "workflow", "[data-wf-report]", After: "Upload photographs");
        if (f.Stage == "Awaiting Upload") return f.Unclassified > 0
            ? new("classify-media", "Classify uploaded photographs", $"{f.Unclassified} photographs are not assigned to Session Plan subjects.", "media", After: "Complete contributions and submit source media")
            : new("upload", "Complete uploads and submit source media", "Verify all required contributions before submitting for review.", "workflow", "[data-wf-action=\"submit-source\"]", After: "Review uploaded photographs");
        if (f.Stage == "Awaiting Production Review") return new("review", "Review uploaded photographs", "Source media is waiting for production review.", "workflow", "[data-wf-action=\"accept-source\"]", After: "Complete production tasks");
        if (f.Stage == "Awaiting Approval") return new("approve", "Approve finished work", "Finished production is waiting for quality approval.", "workflow", "[data-wf-action=\"approve-finished\"]", After: "Deliver finished work");
        if (f.Stage == "Changes Requested") return new("corrections", "Resolve requested corrections", "The latest review requested corrections before work can continue.", "workflow", f.CorrectionKind == "Production" ? "[data-wf-action=\"resume-production\"]" : "[data-wf-action=\"submit-source\"]");
        if (f.Stage == "Ready for Delivery") return new("deliver", "Deliver finished work", "Publish the approved gallery and record the delivery handoff.", "workflow", "[data-wf-action=\"deliver\"]", After: "Close job");
        if (f.Stage == "Delivered") return new("close", "Close job", "Delivery is recorded. Review remaining obligations before closing.", "workflow", "[data-wf-action=\"close\"]");
        var task = f.Tasks.FirstOrDefault(t => t.Eligibility == "InProgress") ?? f.Tasks.FirstOrDefault(t => t.Eligibility == "Available")
            ?? f.Tasks.FirstOrDefault(t => t.Eligibility == "Blocked" && t.Status is not ("Completed" or "Cancelled" or "Skipped"));
        if (task is not null) return new("task:" + task.NodeId, task.Title, task.Reason ?? (task.Eligibility == "InProgress" ? "This workflow task is in progress." : "This is the next eligible workflow task."), "task", TaskId: task.Id, AssignedTo: task.AssignedTo);
        return f.Stage == "Active Production"
            ? new("submit-finished", "Submit finished work", "Review production completion before submitting finished work.", "workflow", "[data-wf-action=\"submit-finished\"]", After: "Approve finished work")
            : new("review-workflow", "Review job requirements", "Review the job workflow to resolve outstanding requirements.", "workflow");
    }
    public static NextActionChoice? Enquiry(bool closed, bool hasClient, bool needsReply, string? quoteStatus)
    {
        if (closed) return null;
        if (!hasClient) return new("match-client", "Assign a client", "The sender has not been matched to a client record.", "association", After: "Reply to enquiry");
        if (needsReply) return new("reply", "Reply to enquiry", "The latest customer message is awaiting a staff reply.", "reply", After: "Confirm requirements and prepare quotation");
        return quoteStatus switch
        {
            "Draft" => new("send-quote", "Complete and send quotation", "A draft quotation matches this enquiry.", "quotes", After: "Follow up quotation"),
            "Queued" => new("follow-up", "Follow up quotation", "The quotation has been queued for delivery. Check delivery status and record the client's response.", "quotes", After: "Record acceptance and prepare booking"),
            "Accepted" => new("prepare-booking", "Prepare booking from accepted quotation", "Acceptance is recorded. Convert the quotation to a booking.", "quotes", After: "Collect deposit"),
            _ => new("requirements", "Confirm requirements and prepare quotation", "The customer has been answered. Confirm the scope before preparing a quotation.", "quotes", After: "Send quotation")
        };
    }
}
