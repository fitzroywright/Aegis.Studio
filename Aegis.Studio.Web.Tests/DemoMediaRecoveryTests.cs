using System.Collections.Concurrent;
using System.Reflection;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Versioning;
using Common.Storage;
using Npgsql;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class DemoMediaRecoveryTests
{
    [StudioPostgresFact]
    public void InterruptedSeedRetryRestoresMissingBytesPreservesVersionIdentityAndJobEdits()
    {
        string schema="studio_recovery_test_"+Guid.NewGuid().ToString("N"),root=Path.Combine(Path.GetTempPath(),schema);
        string connectionString=Environment.GetEnvironmentVariable("STUDIO_TEST_POSTGRES")!;
        using var admin=new NpgsqlConnection(connectionString);admin.Open();Sql(admin,$"create schema {schema}");
        using var source=NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(connectionString){SearchPath=schema}.ConnectionString);
        var database=new StudioDatabase(source);var storage=new LocalFileStorage(root);var versions=new PrefixedFileStorage(storage,"VersionStore");
        Type seed=typeof(DemoDataEndpoints).Assembly.GetType("Aegis.Studio.Web.Operations.DemoDataExpansion")!;
        var add=seed.GetMethod("AddImage",BindingFlags.NonPublic|BindingFlags.Static)!;
        var cache=(ConcurrentDictionary<string,byte[]>)seed.GetField("weddingPhotoCache",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
        string url=((string[])seed.GetField("realPhotoUrls",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!)[0];
        bool cached=cache.TryGetValue(url,out byte[]? previous);cache[url]=[1,2,3,4];
        try
        {
            Guid job=Guid.Parse("de000000-0000-4000-0003-000000000001"),id=Guid.NewGuid(),requestedId=Guid.Parse("de000000-0000-4000-0026-000000000100");
            using var connection=database.OpenConnection();Sql(connection,"create table studio_jobs(id uuid primary key,title text)");Sql(connection,"insert into studio_jobs values($1,'Edited job title')",job);
            var store=new StudioVersionStore(database,storage);
            // Older seed records may already have a non-deterministic ID.
            using(var tx=connection.BeginTransaction()) {id=Add(tx,id,false);tx.Commit();}
            StudioAssetVersion original=store.Get(job,id)!;
            File.Delete(Path.Combine(root,"VersionStore",original.StoredPath));
            Assert.NotNull(storage.GetMetadataAsync("VersionStore/"+original.StoredPath).GetAwaiter().GetResult());
            using(var tx=connection.BeginTransaction()) {Assert.Equal(id,Add(tx,requestedId,true));tx.Commit();}
            using(var file=store.Open(job,id,out var repaired)){Assert.Equal("image/jpeg",repaired.ContentType);Assert.EndsWith(".jpg",repaired.StoredPath);Assert.Equal(original.Id,repaired.Id);Assert.Equal(4,file.Length);}
            Assert.Single(store.GetHistory(job,"demo-1-0"));
            using var title=new NpgsqlCommand("select title from studio_jobs where id=$1",connection);title.Parameters.AddWithValue(job);Assert.Equal("Edited job title",title.ExecuteScalar());
            Sql(connection,"update studio_asset_versions set note='Real user upload' where id=$1",id);
            File.Delete(Path.Combine(root,"VersionStore",store.Get(job,id)!.StoredPath));
            using(var tx=connection.BeginTransaction()) {Assert.Equal(id,Add(tx,requestedId,true));tx.Commit();}
            Assert.Throws<StorageException>(()=>store.Open(job,id,out _));

            Guid Add(NpgsqlTransaction tx,Guid requested,bool repairOnly)=>(Guid)add.Invoke(null,[connection,tx,versions,job,"demo-1-0",requested,1,0,null,"staff",repairOnly,null,null,null])!;
        }
        finally
        {
            if(cached)cache[url]=previous!;else cache.TryRemove(url,out _);
            source.Dispose();Sql(admin,$"drop schema {schema} cascade");if(Directory.Exists(root))Directory.Delete(root,true);
        }
    }
    private static void Sql(NpgsqlConnection connection,string sql,params object[] values){using var command=new NpgsqlCommand(sql,connection);foreach(var value in values)command.Parameters.AddWithValue(value);command.ExecuteNonQuery();}
}
