using Aegis.Studio.Web.WorkDay;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class WorkDayTimeSemanticsTests
{
    private static WorkDayTimePolicy Jamaica(double regularDailyHours = 8) =>
        new("America/Jamaica", regularDailyHours, [DayOfWeek.Saturday, DayOfWeek.Sunday]);

    [Fact]
    public void Multiple_sessions_share_the_same_daily_regular_threshold()
    {
        DateTimeOffset from = new(2026, 9, 14, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset to = from.AddDays(1);
        WorkDayTimeInterval[] intervals =
        [
            new("photo", new DateTimeOffset(2026,9,14,13,0,0,TimeSpan.Zero), new DateTimeOffset(2026,9,14,17,0,0,TimeSpan.Zero)),
            new("photo", new DateTimeOffset(2026,9,14,18,0,0,TimeSpan.Zero), new DateTimeOffset(2026,9,14,23,0,0,TimeSpan.Zero))
        ];

        WorkDayTimeSummary summary = Assert.Single(WorkDayTimeSemantics.Classify(intervals, from, to, Jamaica()));

        Assert.Equal(540, summary.TotalMinutes);
        Assert.Equal(480, summary.RegularMinutes);
        Assert.Equal(60, summary.OvertimeMinutes);
        Assert.Equal(0, summary.WeekendMinutes);
    }

    [Fact]
    public void Weekend_work_is_tracked_without_automatically_becoming_overtime()
    {
        DateTimeOffset start = new(2026, 9, 19, 14, 0, 0, TimeSpan.Zero); // Saturday 09:00 Jamaica
        DateTimeOffset end = start.AddHours(4);

        WorkDayTimeSummary summary = Assert.Single(WorkDayTimeSemantics.Classify(
            [new("photo", start, end)],
            start.AddHours(-1),
            end.AddHours(1),
            Jamaica()));

        Assert.Equal(240, summary.TotalMinutes);
        Assert.Equal(240, summary.RegularMinutes);
        Assert.Equal(0, summary.OvertimeMinutes);
        Assert.Equal(240, summary.WeekendMinutes);
        Assert.Equal(0, summary.WeekendOvertimeMinutes);
    }

    [Fact]
    public void Weekend_overtime_is_the_intersection_not_a_separate_double_count()
    {
        DateTimeOffset start = new(2026, 9, 19, 13, 0, 0, TimeSpan.Zero); // Saturday 08:00 Jamaica
        DateTimeOffset end = start.AddHours(10);

        WorkDayTimeSummary summary = Assert.Single(WorkDayTimeSemantics.Classify(
            [new("photo", start, end)],
            start,
            end,
            Jamaica()));

        Assert.Equal(600, summary.TotalMinutes);
        Assert.Equal(480, summary.RegularMinutes);
        Assert.Equal(120, summary.OvertimeMinutes);
        Assert.Equal(600, summary.WeekendMinutes);
        Assert.Equal(120, summary.WeekendOvertimeMinutes);
    }

    [Fact]
    public void Session_crossing_local_midnight_is_split_between_local_dates()
    {
        DateTimeOffset start = new(2026, 9, 19, 3, 0, 0, TimeSpan.Zero); // Friday 22:00 Jamaica
        DateTimeOffset end = new(2026, 9, 19, 7, 0, 0, TimeSpan.Zero);   // Saturday 02:00 Jamaica

        WorkDayTimeSummary summary = Assert.Single(WorkDayTimeSemantics.Classify(
            [new("photo", start, end)],
            start,
            end,
            Jamaica()));

        Assert.Equal(2, summary.Days.Count);
        Assert.Equal(120, summary.Days.Single(x => x.LocalDate.DayOfWeek == DayOfWeek.Friday).TotalMinutes);
        Assert.Equal(120, summary.Days.Single(x => x.LocalDate.DayOfWeek == DayOfWeek.Saturday).WeekendMinutes);
    }

    [Fact]
    public void Classification_does_not_apply_pay_rates()
    {
        DateTimeOffset start = new(2026, 9, 14, 13, 0, 0, TimeSpan.Zero);
        DateTimeOffset end = start.AddHours(9);

        WorkDayTimeSummary summary = Assert.Single(WorkDayTimeSemantics.Classify(
            [new("photo", start, end)],
            start,
            end,
            Jamaica()));

        Assert.Equal(60, summary.OvertimeMinutes);
        Assert.DoesNotContain(
            typeof(WorkDayTimeSummary).GetProperties(),
            property => property.Name.Contains("Pay", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Contains("Rate", StringComparison.OrdinalIgnoreCase));
    }
}
