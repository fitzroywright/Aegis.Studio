using Aegis.Studio.Web.Persistence;
using Common.Storage;
using Npgsql;

namespace Aegis.Studio.Web.Operations;

public sealed record DemoSeedRunStatus(
    Guid RunId,
    string Status,
    int Percent,
    string Stage,
    int Current,
    int Total,
    string Message,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc = null)
{
    public bool Completed => Status == "Completed";
    public bool Failed => Status == "Failed";
}

public sealed class DemoSeedCoordinator
{
    private readonly StudioDatabase database;
    private readonly IFileStorage storage;
    private readonly ILogger<DemoSeedCoordinator> logger;
    private readonly object gate = new();
    private Guid? activeRunId;
    private Task? activeTask;
    private bool schemaReady;

    public DemoSeedCoordinator(StudioDatabase database, IFileStorage storage, ILogger<DemoSeedCoordinator> logger)
    {
        this.database = database;
        this.storage = storage;
        this.logger = logger;
    }

    public DemoSeedRunStatus Start(string login)
    {
        EnsureSchema();
        lock (gate)
        {
            if (activeTask is { IsCompleted: false } && activeRunId is Guid running)
                return Get(running) ?? throw new InvalidOperationException("The active demo load could not be read.");

            DemoSeedRunStatus? existing = ReadLatestRunning();
            if (existing is not null)
            {
                // A process restart can leave a persisted Running row with no worker.
                // Mark it interrupted and start a fresh idempotent run.
                MarkInterrupted(existing.RunId);
            }

            Guid id = Guid.NewGuid();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Write(new DemoSeedRunStatus(id, "Running", 0, "Preparing", 0, 0,
                "Demo loading has started on the server.", now, now));
            activeRunId = id;
            activeTask = Task.Run(() => Run(id, login));
            return Get(id)!;
        }
    }

    public DemoSeedRunStatus? Get(Guid runId)
    {
        EnsureSchema();
        using var c = database.OpenConnection();
        using var cmd = new NpgsqlCommand("""
            select run_id,status,percent,stage,current_item,total_items,message,
                   started_at_utc,updated_at_utc,completed_at_utc
            from studio_demo_seed_runs where run_id=$1
            """, c);
        cmd.Parameters.AddWithValue(runId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    public DemoSeedRunStatus? Current
    {
        get
        {
            EnsureSchema();
            using var c = database.OpenConnection();
            using var cmd = new NpgsqlCommand("""
                select run_id,status,percent,stage,current_item,total_items,message,
                       started_at_utc,updated_at_utc,completed_at_utc
                from studio_demo_seed_runs
                order by started_at_utc desc
                limit 1
                """, c);
            using var r = cmd.ExecuteReader();
            return r.Read() ? Read(r) : null;
        }
    }

    private void Run(Guid id, string login)
    {
        try
        {
            string result = DemoDataEndpoints.Seed(database, storage, login, progress =>
            {
                Write(new DemoSeedRunStatus(id, "Running", progress.Percent, progress.Stage,
                    progress.Current, progress.Total, progress.Message,
                    Started(id), DateTimeOffset.UtcNow));
            });
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Write(new DemoSeedRunStatus(id, "Completed", 100, "Complete", 0, 0, result,
                Started(id), now, now));
        }
        catch (Exception error)
        {
            logger.LogError(error, "Demo seed failed RunId={RunId}", id);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Write(new DemoSeedRunStatus(id, "Failed", 0, "Failed", 0, 0,
                "Demo data could not be loaded. See server logs for details.",
                Started(id), now, now));
        }
        finally
        {
            lock (gate)
            {
                if (activeRunId == id)
                {
                    activeRunId = null;
                    activeTask = null;
                }
            }
        }
    }

    private DateTimeOffset Started(Guid id) => Get(id)?.StartedAtUtc ?? DateTimeOffset.UtcNow;

    private DemoSeedRunStatus? ReadLatestRunning()
    {
        using var c = database.OpenConnection();
        using var cmd = new NpgsqlCommand("""
            select run_id,status,percent,stage,current_item,total_items,message,
                   started_at_utc,updated_at_utc,completed_at_utc
            from studio_demo_seed_runs
            where status='Running'
            order by started_at_utc desc
            limit 1
            """, c);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    private void MarkInterrupted(Guid id)
    {
        using var c = database.OpenConnection();
        using var cmd = new NpgsqlCommand("""
            update studio_demo_seed_runs
            set status='Failed',stage='Interrupted',
                message='The previous server process ended before this demo load completed. Retry resumes the idempotent seed.',
                updated_at_utc=now(),completed_at_utc=now()
            where run_id=$1 and status='Running'
            """, c);
        cmd.Parameters.AddWithValue(id);
        cmd.ExecuteNonQuery();
    }

    private void Write(DemoSeedRunStatus run)
    {
        EnsureSchema();
        using var c = database.OpenConnection();
        using var cmd = new NpgsqlCommand("""
            insert into studio_demo_seed_runs
              (run_id,status,percent,stage,current_item,total_items,message,started_at_utc,updated_at_utc,completed_at_utc)
            values($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)
            on conflict(run_id) do update set
              status=excluded.status,percent=excluded.percent,stage=excluded.stage,
              current_item=excluded.current_item,total_items=excluded.total_items,
              message=excluded.message,updated_at_utc=excluded.updated_at_utc,
              completed_at_utc=excluded.completed_at_utc
            """, c);
        object?[] values =
        [
            run.RunId, run.Status, run.Percent, run.Stage, run.Current, run.Total, run.Message,
            run.StartedAtUtc, run.UpdatedAtUtc, (object?)run.CompletedAtUtc ?? DBNull.Value
        ];
        foreach (object? value in values) cmd.Parameters.AddWithValue(value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private void EnsureSchema()
    {
        if (schemaReady) return;
        lock (gate)
        {
            if (schemaReady) return;
            using var c = database.OpenConnection();
            using var cmd = new NpgsqlCommand("""
                create table if not exists studio_demo_seed_runs(
                  run_id uuid primary key,
                  status text not null,
                  percent integer not null,
                  stage text not null,
                  current_item integer not null default 0,
                  total_items integer not null default 0,
                  message text not null,
                  started_at_utc timestamptz not null,
                  updated_at_utc timestamptz not null,
                  completed_at_utc timestamptz null
                );
                create index if not exists ix_studio_demo_seed_runs_started
                  on studio_demo_seed_runs(started_at_utc desc);
                """, c);
            cmd.ExecuteNonQuery();
            schemaReady = true;
        }
    }

    private static DemoSeedRunStatus Read(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetInt32(4), r.GetInt32(5),
        r.GetString(6), r.GetFieldValue<DateTimeOffset>(7), r.GetFieldValue<DateTimeOffset>(8),
        r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9));
}
