using Aegis.Studio.Web.Production;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class PerformanceMetricsTests
{
    [Fact]
    public void Editor_metrics_are_based_on_editorial_activity_types()
    {
        string[] types = StudioPerformanceSemantics.EditorActivityTypes;

        Assert.Contains("Cull", types);
        Assert.Contains("Edit", types);
        Assert.Contains("Retouch", types);
        Assert.Contains("Proof", types);
        Assert.Contains("QC", types);
        Assert.DoesNotContain("Shoot", types);
    }

    [Fact]
    public void Production_metrics_are_separate_from_editor_metrics()
    {
        string[] production = StudioPerformanceSemantics.ProductionActivityTypes;

        Assert.Contains("FileIngest", production);
        Assert.Contains("Backup", production);
        Assert.Contains("Delivery", production);
        Assert.Contains("Archive", production);
        Assert.DoesNotContain("Edit", production);
    }

    [Fact]
    public void Performance_contracts_do_not_expose_a_composite_employee_score()
    {
        Assert.DoesNotContain(
            typeof(PhotographerPerformanceMetrics).GetProperties(),
            property => property.Name.Equals("Score", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Equals("Rating", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Equals("Rank", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(
            typeof(ProductionRolePerformanceMetrics).GetProperties(),
            property => property.Name.Equals("Score", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Equals("Rating", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Equals("Rank", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Photographer_metrics_include_field_quality_and_timeliness_evidence()
    {
        string[] names = typeof(PhotographerPerformanceMetrics).GetProperties().Select(x => x.Name).ToArray();

        Assert.Contains("CompletionPercent", names);
        Assert.Contains("OnTimePercent", names);
        Assert.Contains("BackupConfirmedPercent", names);
        Assert.Contains("ReshootPercent", names);
        Assert.Contains("AverageSessionMinutes", names);
    }

    [Fact]
    public void Editor_metrics_include_throughput_timeliness_and_wip_evidence()
    {
        string[] names = typeof(ProductionRolePerformanceMetrics).GetProperties().Select(x => x.Name).ToArray();

        Assert.Contains("CompletionPercent", names);
        Assert.Contains("OpenActivities", names);
        Assert.Contains("BlockedActivities", names);
        Assert.Contains("OverdueActivities", names);
        Assert.Contains("OnTimePercent", names);
        Assert.Contains("AverageCycleMinutes", names);
        Assert.Contains("AverageStartDelayMinutes", names);
    }
}
