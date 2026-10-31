using Aegis.Studio.Domain.Workers;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Production;
using Aegis.Studio.Web.Workers;
using Npgsql;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class WorkerRoleMembershipTests
{
    [StudioPostgresFact]
    public async Task MultipleRolesPersistAndRemovalSurvivesStartupWithoutChangingPhotographyResources()
    {
        await WithDatabase(async (database,source) =>
        {
            var workers=new WorkerDirectory(database);
            workers.SaveWorker(new("jason","Jason","EDITOR",true,"jason@example.test","555-0101"));
            workers.SaveRoles("jason","Jason","jason@example.test",true,["PHOTOGRAPHER","EDITOR","EDITOR"]);
            Assert.Equal(new[]{"EDITOR","PHOTOGRAPHER"},workers.GetRoleMemberships().Select(w=>w.JobRoleCode).OrderBy(x=>x));
            var photos=new PhotographerStore(database);var photo=photos.Get("jason")!;
            Assert.True(photo.IsActive);Guid resource=photo.ScheduleResourceId;
            photos.Save(new("jason","Jason","Freelance",true,["Portraits"],"Lighting kit",resource));
            workers.SaveRoles("jason","Updated Jason","updated@example.test",true,["EDITOR"]);
            Assert.Equal("EDITOR",Assert.Single(workers.GetRoleMemberships()).JobRoleCode);
            Assert.False(photos.Get("jason")!.IsActive);
            await new StudioFeatureDatabase(source).InitializeAsync();
            Assert.Equal("EDITOR",Assert.Single(workers.GetRoleMemberships()).JobRoleCode);
            workers.SaveRoles("jason","Jason","jason@example.test",true,["EDITOR","PHOTOGRAPHER"]);
            var restored=photos.Get("jason")!;
            Assert.Equal(resource,restored.ScheduleResourceId);Assert.Equal("Freelance",restored.EmploymentType);
            Assert.Equal("Lighting kit",restored.DefaultEquipmentNotes);Assert.Equal("Portraits",Assert.Single(restored.Skills));
            Assert.Equal("555-0101",Assert.Single(workers.GetWorkers()).Phone);
            workers.SaveRoles("jason","Jason",null,true,[]);
            await new StudioFeatureDatabase(source).InitializeAsync();
            Assert.Empty(workers.GetRoleMemberships(true));Assert.False(photos.Get("jason")!.IsActive);
        });
    }

    [StudioPostgresFact]
    public async Task InvalidRoleWritesRollbackAndInactiveRoleEditsSurviveSchemaReplay()
    {
        await WithDatabase(async (database,source) =>
        {
            var workers=new WorkerDirectory(database);
            workers.SaveWorker(new("editor","Editor","EDITOR",true));
            Assert.Throws<ArgumentException>(()=>workers.SaveRoles("editor","Changed",null,true,["EDITOR","MISSING"]));
            Assert.Equal("Editor",Assert.Single(workers.GetRoleMemberships()).DisplayName);
            workers.SaveJobRole(new("EDITOR","Image Editor",false,false));
            await new StudioFeatureDatabase(source).InitializeAsync();
            var role=workers.GetTypes(true).Single(r=>r.Code=="EDITOR");Assert.Equal("Image Editor",role.DisplayName);Assert.False(role.IsActive);
            workers.SaveRoles("editor","Editor",null,true,["EDITOR"]); // Retain an existing inactive role.
            Assert.Empty(workers.GetRoleMemberships());Assert.Single(workers.GetRoleMemberships(true));
            Assert.Throws<ArgumentException>(()=>workers.SaveRoles("new","New",null,true,["EDITOR"]));
            workers.SaveRoles("editor","Editor",null,true,[]);Assert.Empty(workers.GetRoleMemberships(true));
        });
    }

    [StudioPostgresFact]
    public async Task MigrationKeepsTheLegacyRoleAndPhotographyRoleForTheSamePerson()
    {
        await WithDatabase(async (database,source) =>
        {
            var workers=new WorkerDirectory(database);workers.SaveWorker(new("sam","Sam","EDITOR",true));
            var photos=new PhotographerStore(database);photos.Save(new("sam","Sam","Employee",true,[],null));
            await using(var c=await source.OpenConnectionAsync())
            await using(var q=new NpgsqlCommand("delete from studio_worker_roles",c))await q.ExecuteNonQueryAsync();
            await new StudioFeatureDatabase(source).InitializeAsync();
            Assert.Equal(new[]{"EDITOR","PHOTOGRAPHER"},workers.GetRoleMemberships().Select(w=>w.JobRoleCode).OrderBy(x=>x));
        });
    }

    private static async Task WithDatabase(Func<StudioDatabase,NpgsqlDataSource,Task> test)
    {
        string connection=Environment.GetEnvironmentVariable("STUDIO_TEST_POSTGRES")!,schema="studio_worker_roles_"+Guid.NewGuid().ToString("N");
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        await using(var create=new NpgsqlCommand($"create schema {schema}",admin))await create.ExecuteNonQueryAsync();
        try
        {
            await using var source=NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(connection){SearchPath=schema}.ConnectionString);
            var database=new StudioDatabase(source);await database.InitializeAsync();await new StudioFeatureDatabase(source).InitializeAsync();
            await test(database,source);
        }
        finally {await using var drop=new NpgsqlCommand($"drop schema if exists {schema} cascade",admin);await drop.ExecuteNonQueryAsync();}
    }
}
