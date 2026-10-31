using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Npgsql;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class StaffPhotoTests
{
    [StudioPostgresFact]
    public async Task StudioPhotoOverridesDirectoryPhotoAndRemovalRevealsDirectoryPhoto()
    {
        string connection=Environment.GetEnvironmentVariable("STUDIO_TEST_POSTGRES")!,schema="studio_staff_photos_"+Guid.NewGuid().ToString("N");
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        await using(var create=new NpgsqlCommand($"create schema {schema}",admin))await create.ExecuteNonQueryAsync();
        try
        {
            await using var source=NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(connection){SearchPath=schema}.ConnectionString);
            var database=new StudioDatabase(source);await database.InitializeAsync();await new StudioFeatureDatabase(source).InitializeAsync();
            var employees=new StudioEmployeeDirectoryStore(database);employees.EnsureSchema();
            employees.Save(new("jason","Jason Brown","jason@example.test"));
            byte[] directory=[0xff,0xd8,0xff,0xd9],studio=[0x89,0x50,0x4e,0x47,0x0d,0x0a,0x1a,0x0a];
            employees.SavePhoto("jason","ActiveDirectory","image/jpeg",null,directory);
            Assert.Equal("ActiveDirectory",employees.GetAll().Single().PhotoSource);
            Assert.Equal(directory,employees.GetPhoto("jason")!.Content);
            employees.SavePhoto("JASON","Studio","image/png","jason.png",studio);
            Assert.Equal("Studio",employees.GetAll().Single().PhotoSource);
            Assert.Equal(studio,employees.GetPhoto("jason")!.Content);
            Assert.True(employees.RemovePhoto("jason"));
            Assert.Equal("ActiveDirectory",employees.GetAll().Single().PhotoSource);
            Assert.Equal(directory,employees.GetPhoto("jason")!.Content);
        }
        finally{await using var drop=new NpgsqlCommand($"drop schema if exists {schema} cascade",admin);await drop.ExecuteNonQueryAsync();}
    }

    [Fact]
    public void StaffPhotoValidationAcceptsOnlySupportedImageSignatures()
    {
        Assert.Equal("image/jpeg",StaffPhotoValidator.DetectContentType([0xff,0xd8,0xff]));
        Assert.Equal("image/png",StaffPhotoValidator.DetectContentType([0x89,0x50,0x4e,0x47,0x0d,0x0a,0x1a,0x0a]));
        Assert.Equal("image/webp",StaffPhotoValidator.DetectContentType("RIFF0000WEBP"u8));
        Assert.Null(StaffPhotoValidator.DetectContentType("not an image"u8));
    }
}
