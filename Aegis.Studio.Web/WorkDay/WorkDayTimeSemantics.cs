namespace Aegis.Studio.Web.WorkDay;

public static class WorkDayTimeSemantics
{
    public static IReadOnlyList<WorkDayTimeSummary> Classify(
        IEnumerable<WorkDayTimeInterval> intervals,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        WorkDayTimePolicy policy)
    {
        if (toUtc <= fromUtc) throw new InvalidOperationException("Time summary end must be after start.");
        if (policy.RegularDailyHours <= 0 || policy.RegularDailyHours > 24)
            throw new InvalidOperationException("RegularDailyHours must be greater than zero and no more than 24.");

        TimeZoneInfo zone = ResolveTimeZone(policy.TimeZoneId);
        HashSet<DayOfWeek> weekend = policy.WeekendDays.ToHashSet();
        double regularDailyMinutes = policy.RegularDailyHours * 60d;

        List<WorkDayTimeBreakdown> dayRows = [];
        foreach (IGrouping<string, WorkDayTimeInterval> userGroup in intervals
            .Where(x => x.EndedAtUtc > fromUtc && x.StartedAtUtc < toUtc)
            .GroupBy(x => x.UserName, StringComparer.OrdinalIgnoreCase))
        {
            var segments = new List<(DateOnly Date, DateTimeOffset StartUtc, DateTimeOffset EndUtc)>();

            foreach (WorkDayTimeInterval interval in userGroup)
            {
                DateTimeOffset start = interval.StartedAtUtc < fromUtc ? fromUtc : interval.StartedAtUtc;
                DateTimeOffset end = interval.EndedAtUtc > toUtc ? toUtc : interval.EndedAtUtc;
                if (end <= start) continue;

                DateTimeOffset cursor = start;
                while (cursor < end)
                {
                    DateTimeOffset local = TimeZoneInfo.ConvertTime(cursor, zone);
                    DateOnly date = DateOnly.FromDateTime(local.DateTime);
                    DateTime localNextMidnight = local.Date.AddDays(1);
                    DateTimeOffset nextBoundaryLocal = new(
                        localNextMidnight,
                        zone.GetUtcOffset(localNextMidnight));
                    DateTimeOffset nextBoundaryUtc = nextBoundaryLocal.ToUniversalTime();
                    DateTimeOffset segmentEnd = nextBoundaryUtc < end ? nextBoundaryUtc : end;
                    if (segmentEnd <= cursor) segmentEnd = end;
                    segments.Add((date, cursor, segmentEnd));
                    cursor = segmentEnd;
                }
            }

            foreach (IGrouping<DateOnly, (DateOnly Date, DateTimeOffset StartUtc, DateTimeOffset EndUtc)> day in
                segments.GroupBy(x => x.Date).OrderBy(x => x.Key))
            {
                double usedRegular = 0d;
                double total = 0d;
                double regular = 0d;
                double overtime = 0d;
                double weekendMinutes = 0d;
                double weekendOvertime = 0d;
                bool isWeekend = weekend.Contains(day.Key.DayOfWeek);

                foreach (var segment in day.OrderBy(x => x.StartUtc))
                {
                    double minutes = Math.Max(0d, (segment.EndUtc - segment.StartUtc).TotalMinutes);
                    total += minutes;

                    double availableRegular = Math.Max(0d, regularDailyMinutes - usedRegular);
                    double segmentRegular = Math.Min(minutes, availableRegular);
                    double segmentOvertime = Math.Max(0d, minutes - segmentRegular);

                    regular += segmentRegular;
                    overtime += segmentOvertime;
                    usedRegular += segmentRegular;

                    if (isWeekend)
                    {
                        weekendMinutes += minutes;
                        weekendOvertime += segmentOvertime;
                    }
                }

                dayRows.Add(new(
                    userGroup.Key,
                    day.Key,
                    Math.Round(total, 2),
                    Math.Round(regular, 2),
                    Math.Round(overtime, 2),
                    Math.Round(weekendMinutes, 2),
                    Math.Round(weekendOvertime, 2)));
            }
        }

        return dayRows
            .GroupBy(x => x.UserName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new WorkDayTimeSummary(
                g.Key,
                fromUtc,
                toUtc,
                Math.Round(g.Sum(x => x.TotalMinutes), 2),
                Math.Round(g.Sum(x => x.RegularMinutes), 2),
                Math.Round(g.Sum(x => x.OvertimeMinutes), 2),
                Math.Round(g.Sum(x => x.WeekendMinutes), 2),
                Math.Round(g.Sum(x => x.WeekendOvertimeMinutes), 2),
                g.OrderBy(x => x.LocalDate).ToArray()))
            .OrderBy(x => x.UserName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static WorkDayTimePolicy FromConfiguration(IConfiguration configuration)
    {
        string timeZoneId = configuration["WorkDay:TimeTracking:TimeZoneId"]?.Trim();
        if (string.IsNullOrWhiteSpace(timeZoneId)) timeZoneId = "America/Jamaica";

        double regularDailyHours = Math.Clamp(
            configuration.GetValue<double?>("WorkDay:TimeTracking:RegularDailyHours") ?? 8d,
            0.25d,
            24d);

        string[] configuredWeekendDays = configuration
            .GetSection("WorkDay:TimeTracking:WeekendDays")
            .Get<string[]>() ?? ["Saturday", "Sunday"];

        DayOfWeek[] weekendDays = configuredWeekendDays
            .Select(x => Enum.TryParse<DayOfWeek>(x, true, out DayOfWeek value) ? value : (DayOfWeek?)null)
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .Distinct()
            .ToArray();

        if (weekendDays.Length == 0) weekendDays = [DayOfWeek.Saturday, DayOfWeek.Sunday];

        return new(timeZoneId, regularDailyHours, weekendDays);
    }

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }
}
