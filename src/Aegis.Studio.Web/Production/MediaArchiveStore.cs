using System.Security.Cryptography;
using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Production;

public sealed class MediaArchiveStore
{
    private readonly StudioDatabase database;
    private readonly string mediaRoot;

    public MediaArchiveStore(StudioDatabase database, IConfiguration configuration, IWebHostEnvironment environment)
    {
        this.database = database;
        string configured = configuration["Storage:MediaArchivePath"] ?? string.Empty;
        mediaRoot = string.IsNullOrWhiteSpace(configured) ? Path.Combine(environment.ContentRootPath, "MediaArchive") : configured;
        Directory.CreateDirectory(mediaRoot);
    }

    public IReadOnlyList<MediaArchiveRecord> GetForJob(Guid jobId)
    {
        using NpgsqlConnection connection = database.OpenConnection(); using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id,job_id,shoot_id,relative_path,backup_relative_path,file_count,total_bytes,manifest_sha256,primary_verified_at_utc,backup_verified_at_utc,created_at_utc,updated_at_utc from studio_media_archives where job_id=$1 order by created_at_utc desc";
        command.Parameters.AddWithValue(jobId); using NpgsqlDataReader reader = command.ExecuteReader(); List<MediaArchiveRecord> values=[]; while(reader.Read()) values.Add(Read(reader)); return values;
    }

    public MediaArchiveRecord Register(Guid jobId, MediaArchiveRegistrationRequest request)
    {
        string relative = NormalizeRelativePath(request.RelativePath);
        string? backup = string.IsNullOrWhiteSpace(request.BackupRelativePath) ? null : NormalizeRelativePath(request.BackupRelativePath);
        MediaArchiveRecord value = new(Guid.NewGuid(),jobId,request.ShootId,relative,backup,0,0,null,null,null,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow);
        Save(value); return value;
    }

    public MediaArchiveRecord Verify(Guid id)
    {
        MediaArchiveRecord current = Get(id) ?? throw new KeyNotFoundException("Media archive record was not found.");
        Verification primary = VerifyDirectory(current.RelativePath);
        Verification? backup = current.BackupRelativePath is null ? null : VerifyDirectory(current.BackupRelativePath);
        MediaArchiveRecord updated = current with
        {
            FileCount=primary.FileCount,TotalBytes=primary.TotalBytes,ManifestSha256=primary.ManifestSha256,
            PrimaryVerifiedAtUtc=primary.VerifiedAtUtc,BackupVerifiedAtUtc=backup?.VerifiedAtUtc,UpdatedAtUtc=DateTimeOffset.UtcNow
        };
        Save(updated); return updated;
    }

    public bool HasVerifiedPrimary(Guid shootId)
    {
        using NpgsqlConnection connection=database.OpenConnection(); using NpgsqlCommand command=connection.CreateCommand();
        command.CommandText="select exists(select 1 from studio_media_archives where shoot_id=$1 and primary_verified_at_utc is not null and file_count>0)"; command.Parameters.AddWithValue(shootId); return command.ExecuteScalar() is bool value && value;
    }

    public bool HasVerifiedBackup(Guid shootId)
    {
        using NpgsqlConnection connection=database.OpenConnection(); using NpgsqlCommand command=connection.CreateCommand();
        command.CommandText="select exists(select 1 from studio_media_archives where shoot_id=$1 and backup_verified_at_utc is not null and file_count>0)"; command.Parameters.AddWithValue(shootId); return command.ExecuteScalar() is bool value && value;
    }

    private Verification VerifyDirectory(string relativePath)
    {
        string full = Resolve(relativePath);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"Media directory does not exist: {relativePath}");
        string[] files = Directory.GetFiles(full,"*",SearchOption.AllDirectories).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).ToArray();
        long total = files.Sum(x=>new FileInfo(x).Length);
        using SHA256 sha = SHA256.Create();
        foreach(string file in files)
        {
            string line = $"{Path.GetRelativePath(full,file).Replace('\\','/')}|{new FileInfo(file).Length}|{File.GetLastWriteTimeUtc(file):O}\n";
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(line); sha.TransformBlock(bytes,0,bytes.Length,null,0);
        }
        sha.TransformFinalBlock([],0,0);
        return new Verification(files.Length,total,Convert.ToHexString(sha.Hash!),DateTimeOffset.UtcNow);
    }

    private MediaArchiveRecord? Get(Guid id)
    {
        using NpgsqlConnection connection=database.OpenConnection(); using NpgsqlCommand command=connection.CreateCommand();
        command.CommandText="select id,job_id,shoot_id,relative_path,backup_relative_path,file_count,total_bytes,manifest_sha256,primary_verified_at_utc,backup_verified_at_utc,created_at_utc,updated_at_utc from studio_media_archives where id=$1"; command.Parameters.AddWithValue(id); using NpgsqlDataReader reader=command.ExecuteReader(); return reader.Read()?Read(reader):null;
    }

    private void Save(MediaArchiveRecord value)
    {
        using NpgsqlConnection connection=database.OpenConnection(); using NpgsqlCommand command=connection.CreateCommand();
        command.CommandText="""
            insert into studio_media_archives(id,job_id,shoot_id,relative_path,backup_relative_path,file_count,total_bytes,manifest_sha256,primary_verified_at_utc,backup_verified_at_utc,created_at_utc,updated_at_utc)
            values($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)
            on conflict(id) do update set relative_path=excluded.relative_path,backup_relative_path=excluded.backup_relative_path,file_count=excluded.file_count,total_bytes=excluded.total_bytes,manifest_sha256=excluded.manifest_sha256,primary_verified_at_utc=excluded.primary_verified_at_utc,backup_verified_at_utc=excluded.backup_verified_at_utc,updated_at_utc=excluded.updated_at_utc
            """;
        object?[] values=[value.Id,value.JobId,(object?)value.ShootId??DBNull.Value,value.RelativePath,(object?)value.BackupRelativePath??DBNull.Value,value.FileCount,value.TotalBytes,(object?)value.ManifestSha256??DBNull.Value,(object?)value.PrimaryVerifiedAtUtc??DBNull.Value,(object?)value.BackupVerifiedAtUtc??DBNull.Value,value.CreatedAtUtc,value.UpdatedAtUtc];
        foreach(object? item in values) command.Parameters.AddWithValue(item??DBNull.Value); command.ExecuteNonQuery();
    }

    private string Resolve(string relative)
    {
        string root = Path.GetFullPath(mediaRoot); string full = Path.GetFullPath(Path.Combine(root,relative));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) && !string.Equals(full,root,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Media path must remain inside the configured media archive root.");
        return full;
    }

    private static string NormalizeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("Media relative path is required.");
        string result=value.Trim().Replace('\\','/').TrimStart('/'); if (result.Contains("..",StringComparison.Ordinal)) throw new InvalidOperationException("Parent path traversal is not allowed."); return result;
    }
    private static MediaArchiveRecord Read(NpgsqlDataReader r)=>new(r.GetGuid(0),r.GetGuid(1),r.IsDBNull(2)?null:r.GetGuid(2),r.GetString(3),r.IsDBNull(4)?null:r.GetString(4),r.GetInt32(5),r.GetInt64(6),r.IsDBNull(7)?null:r.GetString(7),r.IsDBNull(8)?null:r.GetFieldValue<DateTimeOffset>(8),r.IsDBNull(9)?null:r.GetFieldValue<DateTimeOffset>(9),r.GetFieldValue<DateTimeOffset>(10),r.GetFieldValue<DateTimeOffset>(11));
    private sealed record Verification(int FileCount,long TotalBytes,string ManifestSha256,DateTimeOffset VerifiedAtUtc);
}

public sealed record MediaArchiveRecord(Guid Id,Guid JobId,Guid? ShootId,string RelativePath,string? BackupRelativePath,int FileCount,long TotalBytes,string? ManifestSha256,DateTimeOffset? PrimaryVerifiedAtUtc,DateTimeOffset? BackupVerifiedAtUtc,DateTimeOffset CreatedAtUtc,DateTimeOffset UpdatedAtUtc);
public sealed record MediaArchiveRegistrationRequest(Guid? ShootId,string RelativePath,string? BackupRelativePath);
