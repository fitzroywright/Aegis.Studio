using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Production;

public sealed class PerformanceMetricsStore(StudioDatabase database, IConfiguration configuration)
{
    public WorkspacePerformanceSnapshot GetWorkspacePerformance(Guid workspaceId, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        if (toUtc <= fromUtc) throw new InvalidOperationException("Performance window end must be after start.");

        IReadOnlyList<PhotographerPerformanceMetrics> photographers = GetPhotographerMetrics(workspaceId, fromUtc, toUtc);
        IReadOnlyList<ProductionRolePerformanceMetrics> editors = GetProductionMetrics(
            workspaceId,
            fromUtc,
            toUtc,
            StudioPerformanceSemantics.EditorActivityTypes,
            "Editor");

        IReadOnlyList<ProductionRolePerformanceMetrics> production = GetProductionMetrics(
            workspaceId,
            fromUtc,
            toUtc,
            StudioPerformanceSemantics.ProductionActivityTypes,
            "Production");

        return new(workspaceId, fromUtc, toUtc, photographers, editors, production, DateTimeOffset.UtcNow);
    }

    private IReadOnlyList<PhotographerPerformanceMetrics> GetPhotographerMetrics(Guid workspaceId, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        using NpgsqlConnection c = database.OpenConnection();
        using NpgsqlCommand q = c.CreateCommand();
        q.CommandText = """
            select s.photographer_user_name,
                   coalesce(p.display_name,s.photographer_user_name) as display_name,
                   count(*) as assigned_shoots,
                   count(*) filter(where s.status in ('ShootComplete','HandedOff')) as completed_shoots,
                   count(*) filter(where s.checked_in_at_utc is not null) as checked_in_shoots,
                   count(*) filter(where s.checked_in_at_utc is not null and s.checked_in_at_utc <= s.call_time_utc + ($4 * interval '1 minute')) as on_time_checkins,
                   count(*) filter(where s.backup_confirmed) as backup_confirmed,
                   count(*) filter(where s.reshoot_required) as reshoots,
                   coalesce(sum(s.image_count),0) as image_count,
                   avg(extract(epoch from (s.checked_out_at_utc-s.checked_in_at_utc))/60.0)
                     filter(where s.checked_in_at_utc is not null and s.checked_out_at_utc is not null) as avg_session_minutes
            from studio_shoot_sessions s
            join studio_jobs j on j.id=s.job_id
            left join studio_photographers p on lower(p.user_name)=lower(s.photographer_user_name)
            where j.workspace_id=$1
              and s.call_time_utc<$3
              and s.planned_end_utc>$2
              and s.status<>'Cancelled'
            group by s.photographer_user_name,p.display_name
            order by coalesce(p.display_name,s.photographer_user_name)
            """;
        q.Parameters.AddWithValue(workspaceId);
        q.Parameters.AddWithValue(fromUtc);
        q.Parameters.AddWithValue(toUtc);
        q.Parameters.AddWithValue(Math.Clamp(configuration.GetValue<int?>("Performance:PhotographerOnTimeGraceMinutes") ?? 15, 0, 120));

        using NpgsqlDataReader r = q.ExecuteReader();
        List<PhotographerPerformanceMetrics> values = [];
        while (r.Read())
        {
            long assigned = r.GetInt64(2);
            long completed = r.GetInt64(3);
            long checkedIn = r.GetInt64(4);
            long onTime = r.GetInt64(5);
            long backups = r.GetInt64(6);
            long reshoots = r.GetInt64(7);
            long images = r.GetInt64(8);
            double? avgMinutes = r.IsDBNull(9) ? null : Convert.ToDouble(r.GetValue(9), System.Globalization.CultureInfo.InvariantCulture);

            values.Add(new(
                r.GetString(0),
                r.GetString(1),
                assigned,
                completed,
                Ratio(completed, assigned),
                checkedIn,
                onTime,
                Ratio(onTime, checkedIn),
                backups,
                Ratio(backups, completed),
                reshoots,
                Ratio(reshoots, completed),
                images,
                avgMinutes is null ? null : Math.Round(avgMinutes.Value, 1)));
        }
        return values;
    }

    private IReadOnlyList<ProductionRolePerformanceMetrics> GetProductionMetrics(
        Guid workspaceId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        string[] activityTypes,
        string roleName)
    {
        using NpgsqlConnection c = database.OpenConnection();
        using NpgsqlCommand q = c.CreateCommand();
        q.CommandText = """
            select a.assigned_to,
                   coalesce(u.display_name,a.assigned_to) as display_name,
                   count(*) as assigned,
                   count(*) filter(where a.status='Completed') as completed,
                   count(*) filter(where a.status='Blocked') as blocked,
                   count(*) filter(where a.status not in ('Completed','Cancelled')) as open_count,
                   count(*) filter(where a.status not in ('Completed','Cancelled') and a.due_at_utc is not null and a.due_at_utc<now()) as overdue,
                   count(*) filter(where a.status='Completed' and a.due_at_utc is not null and a.completed_at_utc<=a.due_at_utc) as completed_on_time,
                   avg(extract(epoch from (a.completed_at_utc-a.started_at_utc))/60.0)
                     filter(where a.started_at_utc is not null and a.completed_at_utc is not null) as avg_cycle_minutes,
                   avg(extract(epoch from (a.started_at_utc-a.created_at_utc))/60.0)
                     filter(where a.started_at_utc is not null) as avg_start_delay_minutes
            from studio_job_activities a
            join studio_jobs j on j.id=a.job_id
            left join studio_user_profiles u on lower(u.user_name)=lower(a.assigned_to)
            where j.workspace_id=$1
              and a.created_at_utc<$3
              and coalesce(a.completed_at_utc,a.updated_at_utc)>=$2
              and a.assigned_to is not null
              and a.activity_type=any($4)
            group by a.assigned_to,u.display_name
            order by coalesce(u.display_name,a.assigned_to)
            """;
        q.Parameters.AddWithValue(workspaceId);
        q.Parameters.AddWithValue(fromUtc);
        q.Parameters.AddWithValue(toUtc);
        q.Parameters.AddWithValue(activityTypes);

        using NpgsqlDataReader r = q.ExecuteReader();
        List<ProductionRolePerformanceMetrics> values = [];
        while (r.Read())
        {
            long assigned = r.GetInt64(2);
            long completed = r.GetInt64(3);
            long blocked = r.GetInt64(4);
            long open = r.GetInt64(5);
            long overdue = r.GetInt64(6);
            long completedOnTime = r.GetInt64(7);
            double? cycle = r.IsDBNull(8) ? null : Convert.ToDouble(r.GetValue(8), System.Globalization.CultureInfo.InvariantCulture);
            double? startDelay = r.IsDBNull(9) ? null : Convert.ToDouble(r.GetValue(9), System.Globalization.CultureInfo.InvariantCulture);

            values.Add(new(
                roleName,
                r.GetString(0),
                r.GetString(1),
                assigned,
                completed,
                Ratio(completed, assigned),
                open,
                blocked,
                overdue,
                completedOnTime,
                Ratio(completedOnTime, completed),
                cycle is null ? null : Math.Round(cycle.Value, 1),
                startDelay is null ? null : Math.Round(startDelay.Value, 1)));
        }
        return values;
    }

    private static double Ratio(long numerator, long denominator) =>
        denominator <= 0 ? 0d : Math.Round((double)numerator / denominator * 100d, 1);
}

public sealed record WorkspacePerformanceSnapshot(
    Guid WorkspaceId,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    IReadOnlyList<PhotographerPerformanceMetrics> Photographers,
    IReadOnlyList<ProductionRolePerformanceMetrics> Editors,
    IReadOnlyList<ProductionRolePerformanceMetrics> Production,
    DateTimeOffset GeneratedAtUtc);

public sealed record PhotographerPerformanceMetrics(
    string UserName,
    string DisplayName,
    long AssignedShoots,
    long CompletedShoots,
    double CompletionPercent,
    long CheckedInShoots,
    long OnTimeCheckIns,
    double OnTimePercent,
    long BackupConfirmedShoots,
    double BackupConfirmedPercent,
    long ReshootsRequired,
    double ReshootPercent,
    long ImageCount,
    double? AverageSessionMinutes);

public sealed record ProductionRolePerformanceMetrics(
    string Role,
    string UserName,
    string DisplayName,
    long AssignedActivities,
    long CompletedActivities,
    double CompletionPercent,
    long OpenActivities,
    long BlockedActivities,
    long OverdueActivities,
    long CompletedOnTime,
    double OnTimePercent,
    double? AverageCycleMinutes,
    double? AverageStartDelayMinutes);


public static class StudioPerformanceSemantics
{
    public static string[] EditorActivityTypes => ["Cull", "Edit", "Retouch", "Proof", "QC"];
    public static string[] ProductionActivityTypes => ["FileIngest", "Backup", "Print", "Delivery", "Archive", "Other"];
}
