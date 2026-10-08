namespace Aegis.Studio.Web.Workflow;

public sealed record WorkflowTiming(string Light,string Label,double? ElapsedHours,double? OverdueHours);
public sealed record WorkflowThreshold(string Stage,double WarningHours,double OverdueHours,string Policy="Elapsed");

public static class WorkflowRules
{
    public static readonly Guid UnassignedWorkspaceId=Guid.Parse("7fe72808-a713-4d99-8b39-481c86061008");
    public static readonly string[] Stages=["Pending","Needs Assignment","Needs Photographer Assignment","Awaiting Scheduling","Scheduled","Photography In Progress","Awaiting Upload","Awaiting Production Review","Active Production","Awaiting Approval","Changes Requested","Ready for Delivery","Delivered","Completed","Cancelled"];
    public static bool Inactive(string stage)=>stage is "Completed" or "Cancelled";
    public static bool DepositEligible(decimal received)=>received>0;
    public static void ValidateThreshold(WorkflowThreshold t)
    {
        if(!Stages.Contains(t.Stage)||t.Policy!="Elapsed"||!double.IsFinite(t.WarningHours)||!double.IsFinite(t.OverdueHours)||t.WarningHours<=0||t.OverdueHours<=t.WarningHours)
            throw new ArgumentException("Choose an elapsed-clock policy with a positive warning and a later overdue threshold. Business-hours timing is deferred.");
    }
    public static WorkflowTiming Timing(string stage,DateTimeOffset? entered,DateTimeOffset now,WorkflowThreshold? threshold,DateTimeOffset? sessionEnd=null,DateTimeOffset? closed=null)
    {
        double? elapsed=entered.HasValue?Math.Max(0,((closed??now)-entered.Value).TotalHours):null;
        if(Inactive(stage))return new("grey","Inactive",elapsed,null);
        if(!entered.HasValue)return new("neutral","Timing unavailable",null,null);
        if(stage is "Scheduled" or "Photography In Progress")
        {
            if(!sessionEnd.HasValue)return new("neutral","Schedule deadline unavailable",elapsed,null);
            double late=(now-sessionEnd.Value).TotalHours;
            return late>=4?new("red","Overdue",elapsed,late-4):late>=1?new("amber","Attention",elapsed,0):new("green","Normal",elapsed,0);
        }
        if(threshold is null)return new("neutral","Threshold unavailable",elapsed,null);
        return elapsed>=threshold.OverdueHours?new("red","Overdue",elapsed,elapsed-threshold.OverdueHours):elapsed>=threshold.WarningHours?new("amber","Attention",elapsed,0):new("green","Normal",elapsed,0);
    }
    public static string NextAction(string stage)=>stage switch
    {
        "Pending"=>"Review, route and confirm booking", "Needs Assignment"=>"Assign Job Owner",
        "Needs Photographer Assignment"=>"Assign Photographers", "Awaiting Scheduling"=>"Schedule required sessions",
        "Scheduled"=>"Start photography", "Photography In Progress"=>"Finish required sessions", "Awaiting Upload"=>"Complete contributions and submit source media",
        "Awaiting Production Review"=>"Review source media", "Active Production"=>"Complete production tasks and submit finished work",
        "Awaiting Approval"=>"Approve finished work", "Changes Requested"=>"Resolve requested corrections",
        "Ready for Delivery"=>"Publish approved gallery and record delivery", "Delivered"=>"Close Job", _=>"View history"
    };
    public static string Target(string stage,string action,string? correction=null)=> (stage,action) switch
    {
        ("Pending","confirm")=>"Needs Assignment", ("Awaiting Upload","submit-source")=>"Awaiting Production Review",
        ("Changes Requested","submit-source") when correction=="Source"=>"Awaiting Production Review",
        ("Awaiting Production Review","accept-source")=>"Active Production", ("Active Production","submit-finished")=>"Awaiting Approval",
        ("Awaiting Approval","approve-finished")=>"Ready for Delivery", ("Awaiting Production Review","request-changes")=>"Changes Requested",
        ("Awaiting Approval","request-changes")=>"Changes Requested", ("Changes Requested","resume-production") when correction=="Production"=>"Active Production",
        ("Ready for Delivery","deliver")=>"Delivered", ("Delivered","close")=>"Completed",
        (_,"cancel") when !Inactive(stage)=>"Cancelled",
        _=>throw new ArgumentException("This action is unavailable at the current stage. Refresh the Job.")
    };
}
public sealed class WorkflowConflictException : Exception
{
    public WorkflowConflictException():base("This Job changed since you opened it. Refresh before saving your decision."){}
}
