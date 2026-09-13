using System.Security.Cryptography;
using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Versioning;

public sealed class StudioVersionStore
{
    private readonly StudioDatabase database;
    private readonly string root;

    public StudioVersionStore(StudioDatabase database, IConfiguration configuration, IWebHostEnvironment environment)
    {
        this.database = database;
        string configured = configuration["Storage:VersionStorePath"] ?? string.Empty;
        root = Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? Path.Combine(environment.ContentRootPath, "VersionStore") : configured);
        Directory.CreateDirectory(root);
    }

    public IReadOnlyList<StudioAssetVersion> GetHistory(Guid jobId, string assetKey)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id,job_id,asset_key,version_number,file_name,content_type,size_bytes,sha256,stored_path,created_by,created_at_utc,source_version_id,note from studio_asset_versions where job_id=$1 and asset_key=$2 order by version_number desc";
        command.Parameters.AddWithValue(jobId); command.Parameters.AddWithValue(NormalizeKey(assetKey));
        using NpgsqlDataReader reader = command.ExecuteReader(); List<StudioAssetVersion> result=[]; while(reader.Read()) result.Add(Read(reader)); return result;
    }

    public StudioAssetVersion? Get(Guid jobId, Guid versionId)
    {
        using NpgsqlConnection connection=database.OpenConnection(); using NpgsqlCommand command=connection.CreateCommand();
        command.CommandText="select id,job_id,asset_key,version_number,file_name,content_type,size_bytes,sha256,stored_path,created_by,created_at_utc,source_version_id,note from studio_asset_versions where job_id=$1 and id=$2";
        command.Parameters.AddWithValue(jobId); command.Parameters.AddWithValue(versionId); using NpgsqlDataReader reader=command.ExecuteReader(); return reader.Read()?Read(reader):null;
    }

    public StudioAssetVersion Add(Guid jobId,string assetKey,string fileName,string contentType,Stream source,string createdBy,string? note=null,Guid? sourceVersionId=null)
    {
        string key=NormalizeKey(assetKey); Guid id=Guid.NewGuid(); string directory=Path.Combine(root,jobId.ToString("N"),SafeKey(key)); Directory.CreateDirectory(directory);
        string stored=Path.Combine(jobId.ToString("N"),SafeKey(key),id.ToString("N")+Path.GetExtension(fileName)); string full=Resolve(stored);
        using(FileStream output=new(full,FileMode.CreateNew,FileAccess.Write,FileShare.None)) source.CopyTo(output);
        long size=new FileInfo(full).Length; string hash=Hash(full);
        using NpgsqlConnection connection=database.OpenConnection(); using NpgsqlTransaction transaction=connection.BeginTransaction();
        int version;
        using(NpgsqlCommand next=connection.CreateCommand()){next.Transaction=transaction;next.CommandText="select coalesce(max(version_number),0)+1 from studio_asset_versions where job_id=$1 and asset_key=$2";next.Parameters.AddWithValue(jobId);next.Parameters.AddWithValue(key);version=Convert.ToInt32(next.ExecuteScalar());}
        StudioAssetVersion value=new(id,jobId,key,version,Path.GetFileName(fileName),string.IsNullOrWhiteSpace(contentType)?"application/octet-stream":contentType,size,hash,stored,createdBy,DateTimeOffset.UtcNow,sourceVersionId,note);
        Insert(connection,transaction,value); transaction.Commit(); return value;
    }

    public StudioAssetVersion Restore(Guid jobId,Guid versionId,string createdBy,string? note)
    {
        StudioAssetVersion source=Get(jobId,versionId)??throw new KeyNotFoundException("Version was not found.");
        using FileStream input=new(Resolve(source.StoredPath),FileMode.Open,FileAccess.Read,FileShare.Read);
        return Add(jobId,source.AssetKey,source.FileName,source.ContentType,input,createdBy,string.IsNullOrWhiteSpace(note)?$"Restored from version {source.VersionNumber}":note,source.Id);
    }

    public FileStream Open(Guid jobId,Guid versionId,out StudioAssetVersion version)
    {
        version=Get(jobId,versionId)??throw new KeyNotFoundException("Version was not found.");
        return new FileStream(Resolve(version.StoredPath),FileMode.Open,FileAccess.Read,FileShare.Read);
    }

    private static void Insert(NpgsqlConnection c,NpgsqlTransaction t,StudioAssetVersion v){using NpgsqlCommand q=c.CreateCommand();q.Transaction=t;q.CommandText="insert into studio_asset_versions(id,job_id,asset_key,version_number,file_name,content_type,size_bytes,sha256,stored_path,created_by,created_at_utc,source_version_id,note) values($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13)";object?[] a=[v.Id,v.JobId,v.AssetKey,v.VersionNumber,v.FileName,v.ContentType,v.SizeBytes,v.Sha256,v.StoredPath,v.CreatedBy,v.CreatedAtUtc,(object?)v.SourceVersionId??DBNull.Value,(object?)v.Note??DBNull.Value];foreach(object? x in a)q.Parameters.AddWithValue(x??DBNull.Value);q.ExecuteNonQuery();}
    private string Resolve(string relative){string full=Path.GetFullPath(Path.Combine(root,relative));if(!full.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Version path escapes the version store.");return full;}
    private static string NormalizeKey(string value){if(string.IsNullOrWhiteSpace(value))throw new InvalidOperationException("Asset key is required.");return value.Trim();}
    private static string SafeKey(string key)=>Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
    private static string Hash(string path){using FileStream s=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(s));}
    private static StudioAssetVersion Read(NpgsqlDataReader r)=>new(r.GetGuid(0),r.GetGuid(1),r.GetString(2),r.GetInt32(3),r.GetString(4),r.GetString(5),r.GetInt64(6),r.GetString(7),r.GetString(8),r.GetString(9),r.GetFieldValue<DateTimeOffset>(10),r.IsDBNull(11)?null:r.GetGuid(11),r.IsDBNull(12)?null:r.GetString(12));
}

public sealed record StudioAssetVersion(Guid Id,Guid JobId,string AssetKey,int VersionNumber,string FileName,string ContentType,long SizeBytes,string Sha256,string StoredPath,string CreatedBy,DateTimeOffset CreatedAtUtc,Guid? SourceVersionId,string? Note);
