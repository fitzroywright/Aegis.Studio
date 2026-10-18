using System.Text.Json;
using Aegis.Studio.Web.Production;
using Aegis.Studio.Web.WorkDay;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class ClientTerminologyTests
{
    [Fact]
    public void WorkDayAssignmentRoundTripsClientContactFields()
    {
        var assignment = new WorkDayAssignment(Guid.NewGuid(), null, null, "Portrait session",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), null, "Scheduled", null,
            null, null, "Andrea", "andrea@example.test", "876-555-0123", 1);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var payload = JsonSerializer.SerializeToElement(assignment, options);
        Assert.Equal("Andrea", payload.GetProperty("clientName").GetString());
        Assert.Equal("andrea@example.test", payload.GetProperty("clientEmail").GetString());
        Assert.Equal("876-555-0123", payload.GetProperty("clientPhone").GetString());
        Assert.Equal(assignment, payload.Deserialize<WorkDayAssignment>(options));
    }

    [Fact]
    public void WaitingOnClientBlocksDownstreamTasksUntilTheClientResponds()
    {
        var graph = WorkflowGraphExamples.Linear();
        var statuses = new Dictionary<string, string> { ["review"] = "Completed", ["produce"] = "WaitingOnClient" };
        var waiting = WorkflowGraphEngine.Evaluate(graph, statuses, new HashSet<Guid>());
        Assert.Equal("WaitingOnClient", waiting.Nodes.Single(n => n.Id == "produce").Status);
        Assert.Equal("Blocked", waiting.Nodes.Single(n => n.Id == "delivery").Status);
        Assert.False(waiting.Completed);
        statuses["produce"] = "Completed";
        var resumed = WorkflowGraphEngine.Evaluate(graph, statuses, new HashSet<Guid>());
        Assert.Equal("Available", resumed.Nodes.Single(n => n.Id == "delivery").Status);
    }
}
