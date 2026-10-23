namespace Aegis.Studio.Web.Production;

public static class WorkflowGraphExamples
{
    public static WorkflowGraph Linear() => WorkflowGraphEngine.FromSteps([
        new("review", "Confirm the job brief"), new("produce", "Job closed the ordered work", DependsOn: ["review"]),
        new("delivery", "Deliver the gallery and order", "Delivery", DependsOn: ["produce"])]);
    public static WorkflowGraph Graduation(Guid retouch, Guid print)
    {
        var nested = WorkflowGraphEngine.FromSteps([new("shoot", "Photograph scheduled subjects", "Shoot"), new("ingest", "Verify and organize the files", "FileIngest", DependsOn: ["shoot"])]);
        WorkflowNode Task(string id, string label, string type = "Other", string area = "Editing", bool optional = false) => new(id, label, Task: new(id, label, type, area, Optional: optional));
        return new([
            new("start", "Job accepted", "Job accepted"), Task("review", "Confirm the job brief"), new("capture", "Capture and ingest photographs", "Subflow", Subflow: nested),
            new("services", "Confirm ordered services", "Decision"), Task("digital", "Edit and prepare the client gallery"), Task("retouch", "Retouch selected photographs", "Retouch"),
            Task("print", "Produce ordered prints", "Print", "Printing"), new("join", "All selected work complete", "Join"),
            Task("quality", "Quality check and approve the work", "QC", "Quality Control"), Task("delivery", "Deliver the gallery and order", "Delivery"), new("end", "Job closed", "End")
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
        return graph with { Edges = graph.Edges.Select(e => e with { Condition = null }).ToArray(), Nodes = graph.Nodes.Select(n => n.Id == "services" ? n with { Kind = "Fork", Label = "Run ordered services in parallel" } : n).ToArray() };
    }
}
