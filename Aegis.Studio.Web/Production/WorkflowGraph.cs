namespace Aegis.Studio.Web.Production;

public sealed record WorkflowCondition(string Operator, Guid? CatalogItemId = null, WorkflowCondition[]? Conditions = null);
public sealed record WorkflowNode(string Id, string Label, string Kind = "Task", TemplateStep? Task = null,
    string Join = "All", WorkflowGraph? Subflow = null, string? Role = null, double? X = null, double? Y = null);
public sealed record WorkflowEdge(string Id, string From, string To, WorkflowCondition? Condition = null, string? Label = null);
public sealed record WorkflowGraph(WorkflowNode[] Nodes, WorkflowEdge[] Edges);
public sealed record GraphIssue(string NodeId, string Message);
public sealed record GraphNodeState(string Id, string Status, string[] Dependencies, string? Reason = null);
public sealed record GraphEvaluation(GraphNodeState[] Nodes, Dictionary<string, string> Edges, bool Completed);

/// <summary>Pure DAG validation and execution. Layout never participates in execution.</summary>
public static class WorkflowGraphEngine
{
    public static readonly string[] Kinds = ["Start", "Task", "Decision", "Fork", "Join", "Subflow", "End"];
    public static WorkflowGraph FromSteps(TemplateStep[] steps)
    {
        var nodes = new List<WorkflowNode> { new("start", "Start", "Start") };
        nodes.AddRange(steps.Select(s => new WorkflowNode(s.Key, s.Title, Task: s)));
        nodes.Add(new("end", "Complete", "End"));
        var edges = new List<WorkflowEdge>();
        foreach (var s in steps)
            foreach (var p in s.DependsOn is { Length: > 0 } ? s.DependsOn : ["start"])
                edges.Add(new($"{p}>{s.Key}", p, s.Key));
        foreach (var s in steps.Where(s => !steps.Any(other => (other.DependsOn ?? []).Contains(s.Key))))
            edges.Add(new($"{s.Key}>end", s.Key, "end"));
        return new(nodes.ToArray(), edges.ToArray());
    }
    public static GraphIssue[] Validate(WorkflowGraph graph)
    {
        var issues = new List<GraphIssue>();
        ValidateLevel(graph, "", 0, issues);
        return issues.ToArray();
    }
    private static void ValidateLevel(WorkflowGraph g, string prefix, int depth, List<GraphIssue> issues)
    {
        void Error(string id, string message) => issues.Add(new(prefix + id, message));
        if (depth > 8) { Error("", "Subflows may be nested at most eight levels."); return; }
        if (g.Nodes is null || g.Edges is null || g.Nodes.Length is < 2 or > 300 || g.Edges.Length > 1000)
        { Error("", "Provide 2–300 nodes and at most 1,000 connections per flow."); return; }
        var nodes = new Dictionary<string, WorkflowNode>(StringComparer.Ordinal);
        foreach (var n in g.Nodes)
        {
            if (string.IsNullOrWhiteSpace(n.Id) || n.Id.Length > 100 || n.Id.Contains('/') || !nodes.TryAdd(n.Id, n))
                Error(n.Id, "Node IDs must be unique, nonempty and must not contain '/'.");
            if (!Kinds.Contains(n.Kind) || string.IsNullOrWhiteSpace(n.Label) || n.Label.Length > 200) Error(n.Id, "Choose a node type and a label of at most 200 characters.");
            if (n.Join is not ("All" or "Any")) Error(n.Id, "Choose All active branches or Any qualifying branch.");
            if (n.Kind == "Task" && (n.Task is null || n.Task.Key != n.Id || string.IsNullOrWhiteSpace(n.Task.Title))) Error(n.Id, "Task nodes require a task definition with the same stable ID.");
            if (n.Kind == "Subflow")
            {
                if (n.Subflow is null) Error(n.Id, "Select or create an embedded subflow.");
                else ValidateLevel(n.Subflow, prefix + n.Id + "/", depth + 1, issues);
            }
            else if (n.Subflow is not null) Error(n.Id, "Only subflow nodes may contain another workflow.");
        }
        var edgeIds = new HashSet<string>();
        foreach (var e in g.Edges)
        {
            if (string.IsNullOrWhiteSpace(e.Id) || !edgeIds.Add(e.Id)) Error(e.From, "Connection IDs must be unique and nonempty.");
            if (!nodes.ContainsKey(e.From) || !nodes.ContainsKey(e.To) || e.From == e.To) Error(e.From, $"Connection {e.Id} has an invalid destination or creates a self dependency.");
            if (e.Condition is not null && !ValidCondition(e.Condition, 0)) Error(e.From, $"Connection {e.Id} has an invalid service condition.");
        }
        if (issues.Any(i => i.NodeId.StartsWith(prefix, StringComparison.Ordinal))) return;
        var starts = g.Nodes.Where(n => n.Kind == "Start").ToArray();
        var ends = g.Nodes.Where(n => n.Kind == "End").ToArray();
        if (starts.Length != 1 || ends.Length != 1) { Error("", "Each flow needs exactly one Start and one completion node."); return; }
        if (g.Edges.Any(e => e.To == starts[0].Id || e.From == ends[0].Id)) Error(starts[0].Id, "Start cannot have incoming connections; completion cannot have outgoing connections.");
        var sorted = Sort(g);
        if (sorted.Count != nodes.Count) Error("", "Circular dependencies are not allowed. Remove the connection that loops back.");
        var reachable = Reach(starts[0].Id, g.Edges, false);
        var terminating = Reach(ends[0].Id, g.Edges, true);
        foreach (var n in g.Nodes)
        {
            if (!reachable.Contains(n.Id)) Error(n.Id, "This node is disconnected from Start.");
            if (!terminating.Contains(n.Id)) Error(n.Id, "Connect this branch to a join or completion point.");
            if (n.Kind == "Decision" && !g.Edges.Any(e => e.From == n.Id && e.Condition is null)) Error(n.Id, "Provide an unconditional continuation so an unselected service cannot deadlock the flow.");
            if (n.Kind == "Join" && g.Edges.Count(e => e.To == n.Id) < 2) Error(n.Id, "A join needs at least two incoming branches.");
        }
    }
    private static HashSet<string> Reach(string start, WorkflowEdge[] edges, bool reverse)
    {
        var visited = new HashSet<string>(); var pending = new Stack<string>(); pending.Push(start);
        while (pending.TryPop(out var id)) if (visited.Add(id)) foreach (var e in edges.Where(e => (reverse ? e.To : e.From) == id)) pending.Push(reverse ? e.From : e.To);
        return visited;
    }
    private static bool ValidCondition(WorkflowCondition c, int depth) => depth <= 8 && c.Operator switch
    {
        "ServiceSelected" => c.CatalogItemId.HasValue && c.CatalogItemId != Guid.Empty && (c.Conditions?.Length ?? 0) == 0,
        "Not" => c.Conditions is { Length: 1 } && ValidCondition(c.Conditions[0], depth + 1),
        "All" or "Any" => c.Conditions is { Length: > 0 and <= 20 } && c.Conditions.All(x => ValidCondition(x, depth + 1)),
        _ => false
    };
    public static bool Matches(WorkflowCondition? c, ISet<Guid> services) => c is null || c.Operator switch
    {
        "ServiceSelected" => c.CatalogItemId.HasValue && services.Contains(c.CatalogItemId.Value),
        "Not" => !Matches(c.Conditions![0], services),
        "All" => c.Conditions!.All(x => Matches(x, services)),
        "Any" => c.Conditions!.Any(x => Matches(x, services)),
        _ => throw new ArgumentException("Invalid workflow condition.")
    };
    public static WorkflowGraph Flatten(WorkflowGraph graph)
    {
        var nodes = new List<WorkflowNode>(); var edges = new List<WorkflowEdge>();
        void Add(WorkflowGraph flow, string prefix)
        {
            foreach (var n in flow.Nodes)
            {
                if (n.Kind == "Subflow" && n.Subflow is not null) Add(n.Subflow, prefix + n.Id + "/");
                else nodes.Add(n with { Id = prefix + n.Id, Task = n.Task is null ? null : n.Task with { Key = prefix + n.Id } });
            }
            string Endpoint(string id, bool entering)
            {
                var n = flow.Nodes.Single(x => x.Id == id);
                return n.Kind == "Subflow" && n.Subflow is not null
                    ? prefix + id + "/" + n.Subflow.Nodes.Single(x => x.Kind == (entering ? "Start" : "End")).Id : prefix + id;
            }
            edges.AddRange(flow.Edges.Select(e => e with { Id = prefix + e.Id, From = Endpoint(e.From, false), To = Endpoint(e.To, true) }));
        }
        Add(graph, ""); return new(nodes.ToArray(), edges.ToArray());
    }
    public static List<WorkflowNode> Sort(WorkflowGraph graph)
    {
        var degree = graph.Nodes.ToDictionary(n => n.Id, n => graph.Edges.Count(e => e.To == n.Id));
        var ready = new Queue<WorkflowNode>(graph.Nodes.Where(n => degree[n.Id] == 0)); var result = new List<WorkflowNode>();
        while (ready.TryDequeue(out var n))
        {
            result.Add(n);
            foreach (var e in graph.Edges.Where(e => e.From == n.Id)) if (--degree[e.To] == 0) ready.Enqueue(graph.Nodes.Single(x => x.Id == e.To));
        }
        return result;
    }
    public static GraphEvaluation Evaluate(WorkflowGraph definition, IReadOnlyDictionary<string, string> tasks, ISet<Guid> services)
    {
        var graph = Flatten(definition); var states = new Dictionary<string, GraphNodeState>(); var edges = new Dictionary<string, string>();
        foreach (var n in Sort(graph))
        {
            var incoming = graph.Edges.Where(e => e.To == n.Id).ToArray();
            var applicable = incoming.Where(e => edges[e.Id] != "Inactive").ToArray();
            bool ready = n.Join == "Any" ? applicable.Any(e => edges[e.Id] == "Completed") : applicable.Length > 0 && applicable.All(e => edges[e.Id] == "Completed");
            bool root = incoming.Length == 0;
            bool inactive = !root && applicable.Length == 0;
            string status = inactive ? "Inactive" : !ready && !root ? "Blocked" : n.Kind == "Task" ? "Available" : "Completed";
            if (n.Kind == "Task" && tasks.TryGetValue(n.Id, out var taskStatus))
            {
                // Started work remains applicable after a service removal until explicitly reconciled.
                if (taskStatus is "Completed" or "InProgress" or "Blocked" or "WaitingOnCustomer" or "ReworkRequired" or "Partial" or "Failed" or "ReprintRequired" or "ReadyForDelivery") status = taskStatus;
                else if (taskStatus == "Cancelled" && n.Task?.Optional == true) status = "Skipped";
            }
            states[n.Id] = new(n.Id, status, incoming.Select(e => e.From).ToArray(), status == "Blocked" ? "Waiting for active prerequisites" : inactive ? "Service branch is inactive" : null);
            foreach (var e in graph.Edges.Where(e => e.From == n.Id))
                edges[e.Id] = !Matches(e.Condition, services) || status == "Inactive" ? "Inactive" : status is "Completed" or "Skipped" ? "Completed" : "Pending";
        }
        var required = graph.Nodes.Where(n => n.Kind == "Task").Select(n => states[n.Id]);
        bool completed = states.Values.Where(s => graph.Nodes.Any(n => n.Id == s.Id && n.Kind == "End")).All(s => s.Status is "Completed" or "Inactive")
            && required.All(s => s.Status is "Completed" or "Skipped" or "Inactive");
        return new(states.Values.ToArray(), edges, completed);
    }
}
