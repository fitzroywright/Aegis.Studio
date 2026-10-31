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
    public async Task GlobalAccessRolesCanBeCreatedAndEditedWithoutLosingAssignments()
    {
        string connection=Environment.GetEnvironmentVariable("STUDIO_TEST_POSTGRES")!,schema="studio_access_roles_"+Guid.NewGuid().ToString("N");
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        await using(var create=new NpgsqlCommand($"CREATE SCHEMA {schema}",admin))await create.ExecuteNonQueryAsync();
        try
        {
            await using var source=NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(connection){SearchPath=schema}.ConnectionString);
            var store=new GlobalOperationalRoleStore(new StudioDatabase(source));store.EnsureSchema();
            var created=store.CreateRole(new GlobalOperationalRoleDefinitionRequest("Editor","Edits assigned work",[StudioPermissions.Access,StudioPermissions.ViewAllJobs],true));
            store.SaveMember(created.Id,new GlobalOperationalRoleMemberRequest("domain/editor","Studio Editor",true));
            var updated=store.UpdateRole(created.Id,new GlobalOperationalRoleDefinitionRequest("Senior Editor","Reviews and edits work",[StudioPermissions.Access,StudioPermissions.AddClientAttachments],true));
            Assert.Equal("Senior Editor",updated.Name);Assert.Contains(StudioPermissions.AddClientAttachments,updated.Permissions);Assert.DoesNotContain(StudioPermissions.ViewAllJobs,updated.Permissions);
            Assert.Equal("domain/editor",Assert.Single(store.GetMembers(created.Id)).UserName);
            Assert.Throws<InvalidOperationException>(()=>store.UpdateRole(created.Id,new GlobalOperationalRoleDefinitionRequest("Senior Editor",null,["unsupported.permission"],true)));
        }
        finally{await using var drop=new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE",admin);await drop.ExecuteNonQueryAsync();}
    }

    [StudioPostgresFact]
    public async Task WorkspaceAndJobPermissionWritesShareTheAccountAuditAndRollbackBoundary()
    {
        string connection=Environment.GetEnvironmentVariable("STUDIO_TEST_POSTGRES")!,schema="studio_permission_audit_"+Guid.NewGuid().ToString("N");
        await using var admin=new NpgsqlConnection(connection);await admin.OpenAsync();
        await using(var create=new NpgsqlCommand($"CREATE SCHEMA {schema}",admin))await create.ExecuteNonQueryAsync();
        try
        {
            await using var source=NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(connection){SearchPath=schema}.ConnectionString);
            var database=new StudioDatabase(source);await database.InitializeAsync();await new StudioFeatureDatabase(source).InitializeAsync();
            var accounts=new RelationalAccountStore(()=>source.CreateConnection(),"Aegis.Studio");accounts.EnsureSchema();
            var proof=new VerifiedIdentity(new ExternalIdentityKey("active-directory","domain","subject"),AuthenticationProviderKind.ActiveDirectory,"preserved-alias","Staff",DateTimeOffset.UtcNow,"ldaps");
            var account=accounts.Resolve(proof,AccountPopulation.Staff,true).Account;accounts.SetStatus(account.Id,SecurityAccountStatus.Active,"approved-admin");
            var actor=new ClaimsPrincipal(new ClaimsIdentity(new[]{new Claim(AccountSecurityHosting.AccountIdClaim,account.Id)},"test"));
            Guid workspace=Guid.NewGuid(),conversation=Guid.NewGuid(),job=Guid.NewGuid();
            await using(var c=await source.OpenConnectionAsync())
            {
                using(var q=new NpgsqlCommand("insert into studio_workspaces(id,name,created_at_utc,updated_at_utc) values($1,'Test',now(),now())",c))
                {q.Parameters.AddWithValue(workspace);await q.ExecuteNonQueryAsync();}
                using(var q=new NpgsqlCommand("insert into studio_conversations(id,workspace_id,subject,contact_name,contact_address,channel,created_at_utc) values($1,$2,'Test','Client','client@example.test','Email',now())",c))
                {q.Parameters.AddWithValue(conversation);q.Parameters.AddWithValue(workspace);await q.ExecuteNonQueryAsync();}
                using(var q=new NpgsqlCommand("insert into studio_jobs(id,workspace_id,conversation_id,title,client_name,status,created_at_utc) values($1,$2,$3,'Test','Client','New',now())",c))
                {q.Parameters.AddWithValue(job);q.Parameters.AddWithValue(workspace);q.Parameters.AddWithValue(conversation);await q.ExecuteNonQueryAsync();}
            }
            var workspaces=new WorkspaceAccessService(database);var jobs=new JobAccessService(database);
            var saved=StudioStaffAuthorization.Change(accounts,actor,account.LoginName,(c,t,alias)=>workspaces.SaveMember(workspace,new WorkspaceMemberRequest(alias,"Staff",true,WorkspacePermissionSet.ViewOnly),c,t));
            Assert.Equal(account.LoginName,saved.UserName);Assert.Single(workspaces.GetMembers(workspace));
            long stamp=accounts.Find(account.Id)!.SessionVersion;
            Assert.Throws<InvalidOperationException>(()=>StudioStaffAuthorization.Change<bool>(accounts,actor,account.LoginName,(c,t,alias)=>
            {jobs.SaveMember(job,new JobMemberRequest(alias,"Staff",true,false,JobPermissionSet.Viewer),c,t);throw new InvalidOperationException("Interrupted write.");}));
            Assert.Empty(jobs.GetMembers(job));Assert.Equal(stamp,accounts.Find(account.Id)!.SessionVersion);
            var jobMember=StudioStaffAuthorization.Change(accounts,actor,account.LoginName,(c,t,alias)=>jobs.SaveMember(job,new JobMemberRequest(alias,"Staff",true,false,JobPermissionSet.Viewer),c,t));
            Assert.Equal(account.LoginName,jobMember.UserName);Assert.Single(jobs.GetMembers(job));Assert.Equal(stamp+1,accounts.Find(account.Id)!.SessionVersion);
            await using var auditConnection=await source.OpenConnectionAsync();
            await using var audit=new NpgsqlCommand("SELECT COUNT(*) FROM common_security_account_events WHERE action='ApplicationAuthorizationChanged'",auditConnection);
            Assert.Equal(2L,await audit.ExecuteScalarAsync());
        }
        finally{await using var drop=new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE",admin);await drop.ExecuteNonQueryAsync();}
    }

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
            long version=accounts.Find(account.Id)!.SessionVersion;
            accounts.ChangeAuthorization(account.Id,"approved-admin",(connection,transaction)=>roles.RemoveMember(Guid.Parse("f40f8c48-233b-49f4-a1d3-5ba85d6fc002"),account.LoginName,(NpgsqlConnection)connection,(NpgsqlTransaction)transaction));
            Assert.False(new AccountProvisioningService(accounts).IsCurrentSession(account.Id,version));
            Assert.Null(await StudioSignIn.CreatePrincipalAsync(proof,services,default));
            long afterRevoke=accounts.Find(account.Id)!.SessionVersion;
            Assert.Throws<InvalidOperationException>(()=>accounts.ChangeAuthorization(account.Id,"approved-admin",(connection,transaction)=>
            {
                roles.SaveMember(Guid.Parse("f40f8c48-233b-49f4-a1d3-5ba85d6fc002"),new GlobalOperationalRoleMemberRequest(account.LoginName,"Staff",true),(NpgsqlConnection)connection,(NpgsqlTransaction)transaction);
                throw new InvalidOperationException("Audit/role transaction interrupted.");
            }));
            Assert.Equal(afterRevoke,accounts.Find(account.Id)!.SessionVersion);
            Assert.Null(await StudioSignIn.CreatePrincipalAsync(proof,services,default));
            accounts.SetStatus(account.Id,SecurityAccountStatus.Disabled,"approved-admin");Assert.Null(await StudioSignIn.CreatePrincipalAsync(proof,services,default));
        }
        finally{await using var drop=new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE",admin);await drop.ExecuteNonQueryAsync();}
    }
}

