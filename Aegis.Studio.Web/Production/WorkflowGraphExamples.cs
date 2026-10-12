namespace Aegis.Studio.Web.Production;

public static class WorkflowGraphExamples
{
    public static WorkflowGraph Linear() => WorkflowGraphEngine.FromSteps([
        new("review", "Review job requirements"), new("produce", "Produce deliverables", DependsOn: ["review"]),
        new("delivery", "Deliver to client", "Delivery", DependsOn: ["produce"])]);
    public static WorkflowGraph Graduation(Guid retouch, Guid print)
    {
        var nested = WorkflowGraphEngine.FromSteps([new("shoot", "Photograph subjects", "Shoot"), new("ingest", "Verify uploaded files", "FileIngest", DependsOn: ["shoot"])]);
        WorkflowNode Task(string id, string label, string type = "Other", string area = "Editing", bool optional = false) => new(id, label, Task: new(id, label, type, area, Optional: optional));
        return new([
            new("start", "Start", "Start"), Task("review", "Review job requirements"), new("capture", "Photography and upload", "Subflow", Subflow: nested),
            new("services", "Selected services", "Decision"), Task("digital", "Prepare digital gallery"), Task("retouch", "Professional retouching", "Retouch"),
            Task("print", "Print photographs", "Print", "Printing"), new("join", "All selected work ready", "Join"),
            Task("quality", "Approve deliverables", "QC", "Quality Control"), Task("delivery", "Deliver to client", "Delivery"), new("end", "Complete", "End")
        ],[
            new("start-review", "start", "review"), new("review-capture", "review", "capture"), new("capture-services", "capture", "services"),
            new("services-digital", "services", "digital"), new("services-retouch", "services", "retouch", new("ServiceSelected", retouch), "Retouching selected"),
            new("services-print", "services", "print", new("ServiceSelected", print), "Printing selected"),
            new("digital-join", "digital", "join"), new("retouch-join", "retouch", "join"), new("print-join", "print", "join"),
            new("join-quality", "join", "quality"), new("quality-delivery", "quality", "delivery"), new("delivery-end", "delivery", "end")
        ]);
    }
    public static WorkflowGraph Parallel()
    {
        var graph = Graduation(Guid.NewGuid(), Guid.NewGuid());
        return graph with { Edges = graph.Edges.Select(e => e with { Condition = null }).ToArray(), Nodes = graph.Nodes.Select(n => n.Id == "services" ? n with { Kind = "Fork", Label = "Parallel production" } : n).ToArray() };
    }
}
