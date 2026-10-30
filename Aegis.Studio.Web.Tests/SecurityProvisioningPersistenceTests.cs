using System.Security.Claims;
using Aegis.Studio.Web.Authentication;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Common.Security.Abstractions;
using Common.Security.Hosting;
using Common.Security.Integration;
using Common.Security.Models;
using Common.Security.Services;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Aegis.Studio.Web.Tests;
public sealed class SecurityProvisioningPersistenceTests
{
    [StudioPostgresFact]
    public async Task PendingStaffGetsNoDefaultRoleAndApprovedAliasAndBusinessRolesArePreserved()
    {
        string connection=Environment.GetEnvironmentVariable("STUDIO_TEST_POSTGRES")!;
        string schema="studio_security_"+Guid.NewGuid().ToString("N");
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        await using(var create=new NpgsqlCommand($"CREATE SCHEMA {schema}",admin))await create.ExecuteNonQueryAsync();
        try
        {
            await using var source=NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(connection){SearchPath=schema}.ConnectionString);
            var database=new StudioDatabase(source);var accounts=new RelationalAccountStore(()=>source.CreateConnection(),"Aegis.Studio");accounts.EnsureSchema();
            var roles=new GlobalOperationalRoleStore(database);roles.EnsureSchema();
            using var services=new ServiceCollection().AddSingleton(database).AddSingleton<IAccountStore>(accounts).AddSingleton(new AccountProvisioningService(accounts)).BuildServiceProvider();
            var proof=new VerifiedIdentity(new ExternalIdentityKey("active-directory","domain-guid","user-guid"),AuthenticationProviderKind.ActiveDirectory,"original-alias","Staff",DateTimeOffset.UtcNow,"ldaps",groups:["Domain Admins"]);
            var pending=await StudioSignIn.CreatePrincipalAsync(proof,services,default);
            Assert.NotNull(pending);Assert.Empty(pending.FindAll(ClaimTypes.Role));Assert.Equal("PendingAccess",pending.FindFirstValue("account_state"));
            var account=Assert.Single(accounts.List());accounts.SetStatus(account.Id,SecurityAccountStatus.Active,"approved-admin");
            Assert.Null(await StudioSignIn.CreatePrincipalAsync(proof,services,default));
            roles.SaveMember(Guid.Parse("f40f8c48-233b-49f4-a1d3-5ba85d6fc002"),new GlobalOperationalRoleMemberRequest("original-alias","Staff",true));
            var approved=await StudioSignIn.CreatePrincipalAsync(proof,services,default);
            Assert.NotNull(approved);Assert.Equal("original-alias",approved.FindFirstValue(ClaimTypes.NameIdentifier));Assert.Contains(approved.FindAll(ClaimTypes.Role),x=>x.Value=="User");
            var renamed=new VerifiedIdentity(proof.Key,proof.ProviderKind,"renamed-directory-user","Staff",DateTimeOffset.UtcNow,"ldaps");
            Assert.Equal("original-alias",(await StudioSignIn.CreatePrincipalAsync(renamed,services,default))!.FindFirstValue(ClaimTypes.NameIdentifier));
            accounts.SetStatus(account.Id,SecurityAccountStatus.Disabled,"approved-admin");Assert.Null(await StudioSignIn.CreatePrincipalAsync(proof,services,default));
        }
        finally{await using var drop=new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE",admin);await drop.ExecuteNonQueryAsync();}
    }
}
