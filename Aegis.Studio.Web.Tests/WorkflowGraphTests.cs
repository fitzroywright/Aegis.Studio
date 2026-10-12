using Aegis.Studio.Web.Production;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public class WorkflowGraphTests
{
    private readonly Guid retouch = Guid.NewGuid(), print = Guid.NewGuid();
    private static Dictionary<string,string> States(params string[] completed) => completed.ToDictionary(id => id, _ => "Completed");
    private static string State(GraphEvaluation result, string id) => result.Nodes.Single(n => n.Id == id).Status;
    [Fact] public void SequentialTasksFollowDependenciesRegardlessOfPositionOrArrayOrder()
    {
        var g = WorkflowGraphExamples.Linear(); g = g with { Nodes = g.Nodes.Reverse().Select(n => n with { X = -100, Y = 99 }).ToArray() };
        Assert.Empty(WorkflowGraphEngine.Validate(g));
        var result = WorkflowGraphEngine.Evaluate(g, States(), new HashSet<Guid>());
        Assert.Equal("Available",State(result,"review")); Assert.Equal("Blocked",State(result,"produce"));
        result=WorkflowGraphEngine.Evaluate(g,States("review"),new HashSet<Guid>());
        Assert.Equal("Available",State(result,"produce"));Assert.Equal("Blocked",State(result,"delivery"));Assert.False(result.Completed);
        Assert.True(WorkflowGraphEngine.Evaluate(g,States("review","produce","delivery"),new HashSet<Guid>()).Completed);
    }
    [Fact] public void ParallelBranchesBecomeAvailableTogetherAndAllJoinWaits()
    {
        var g=WorkflowGraphExamples.Parallel();Assert.Empty(WorkflowGraphEngine.Validate(g));
        var done=States("review","capture/shoot","capture/ingest");var result=WorkflowGraphEngine.Evaluate(g,done,new HashSet<Guid>());
        Assert.All(new[]{"digital","retouch","print"},id=>Assert.Equal("Available",State(result,id)));
        done["digital"]="Completed";done["retouch"]="Completed";
        result=WorkflowGraphEngine.Evaluate(g,done,new HashSet<Guid>());Assert.Equal("Blocked",State(result,"quality"));
        done["print"]="Completed";Assert.Equal("Available",State(WorkflowGraphEngine.Evaluate(g,done,new HashSet<Guid>()),"quality"));
    }
    [Theory] [InlineData(false,false)] [InlineData(true,false)] [InlineData(false,true)] [InlineData(true,true)]
    public void PosSelectionsActivateExactlyTheirBranches(bool selectedRetouch,bool selectedPrint)
    {
        var services=new HashSet<Guid>();if(selectedRetouch)services.Add(retouch);if(selectedPrint)services.Add(print);
        var g=WorkflowGraphExamples.Graduation(retouch,print);Assert.Empty(WorkflowGraphEngine.Validate(g));
        var states=States("review","capture/shoot","capture/ingest","digital");
        var result=WorkflowGraphEngine.Evaluate(g,states,services);
        Assert.Equal(selectedRetouch?"Available":"Inactive",State(result,"retouch"));
        Assert.Equal(selectedPrint?"Available":"Inactive",State(result,"print"));
        Assert.Equal(selectedRetouch||selectedPrint?"Blocked":"Available",State(result,"quality"));
        Assert.Single(WorkflowGraphEngine.Flatten(g).Nodes.Where(n=>n.Id=="join"));
    }
    [Fact] public void AnyJoinContinuesAfterOneQualifyingBranchWhileCompletionStillRequiresActiveTasks()
    {
        var g=WorkflowGraphExamples.Parallel();g=g with{Nodes=g.Nodes.Select(n=>n.Id=="join"?n with{Join="Any"}:n).ToArray()};
        var result=WorkflowGraphEngine.Evaluate(g,States("review","capture/shoot","capture/ingest","digital"),new HashSet<Guid>());
        Assert.Equal("Available",State(result,"quality"));Assert.False(result.Completed);
    }
    [Fact] public void OptionalSkipDoesNotDeadlockDownstreamAndRequiredCancellationDoesNotComplete()
    {
        var g=WorkflowGraphExamples.Linear();g=g with{Nodes=g.Nodes.Select(n=>n.Id=="produce"?n with{Task=n.Task! with{Optional=true}}:n).ToArray()};
        var statuses=States("review");statuses["produce"]="Cancelled";
        Assert.Equal("Available",State(WorkflowGraphEngine.Evaluate(g,statuses,new HashSet<Guid>()),"delivery"));
        var required=WorkflowGraphExamples.Linear();Assert.Equal("Blocked",State(WorkflowGraphEngine.Evaluate(required,statuses,new HashSet<Guid>()),"delivery"));
    }
    [Fact] public void NestedFlowsRetainIncomingAndOutgoingConnectionsWithoutSharedTaskDuplication()
    {
        var g=WorkflowGraphExamples.Graduation(retouch,print);var flattened=WorkflowGraphEngine.Flatten(g);
        Assert.Contains(flattened.Edges,e=>e.From=="review"&&e.To=="capture/start");
        Assert.Contains(flattened.Edges,e=>e.From=="capture/end"&&e.To=="services");
        Assert.Equal(flattened.Nodes.Length,flattened.Nodes.Select(n=>n.Id).Distinct().Count());
        Assert.Equal("Blocked",State(WorkflowGraphEngine.Evaluate(g,States("review","capture/shoot"),new HashSet<Guid>()),"digital"));
    }
    [Fact] public void StartedAndCompletedBranchWorkSurvivesServiceRemoval()
    {
        var g=WorkflowGraphExamples.Graduation(retouch,print);var states=States("review","capture/shoot","capture/ingest");states["retouch"]="InProgress";
        var result=WorkflowGraphEngine.Evaluate(g,states,new HashSet<Guid>());
        Assert.Equal("InProgress",State(result,"retouch"));Assert.Equal("Blocked",State(result,"quality"));
        states["retouch"]="Completed";Assert.Equal("Completed",State(WorkflowGraphEngine.Evaluate(g,states,new HashSet<Guid>()),"retouch"));
    }
    [Fact] public void InvalidNodesConnectionsCyclesConditionsAndTerminationAreRejected()
    {
        var g=WorkflowGraphExamples.Linear();
        Assert.NotEmpty(WorkflowGraphEngine.Validate(g with{Edges=[..g.Edges,new("bad","missing","end")]}));
        Assert.NotEmpty(WorkflowGraphEngine.Validate(g with{Edges=[..g.Edges,new("cycle","produce","review")]}));
        Assert.NotEmpty(WorkflowGraphEngine.Validate(g with{Nodes=[..g.Nodes,new("orphan","Orphan",Task:new("orphan","Orphan"))]}));
        Assert.NotEmpty(WorkflowGraphEngine.Validate(g with{Edges=g.Edges.Select(e=>e.From=="delivery"?e with{Condition=new("EvalScript")}:e).ToArray()}));
        Assert.NotEmpty(WorkflowGraphEngine.Validate(g with{Edges=g.Edges.Where(e=>e.To!="end").ToArray()}));
        Assert.NotEmpty(WorkflowGraphEngine.Validate(g with{Nodes=[..g.Nodes,new("broken","Missing subflow","Subflow")]}));
    }
    [Fact] public void ComposedConditionsUseStableCatalogIds()
    {
        var both=new WorkflowCondition("All",Conditions:[new("ServiceSelected",retouch),new("Not",Conditions:[new("ServiceSelected",print)])]);
        Assert.True(WorkflowGraphEngine.Matches(both,new HashSet<Guid>{retouch}));Assert.False(WorkflowGraphEngine.Matches(both,new HashSet<Guid>{retouch,print}));
    }
}
