using Aegis.Studio.Web.Authentication;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Common.Security.Abstractions;
using Common.Security.Authenticators;
using Common.Security.Hosting;
using Common.Security.Integration;
using Common.Security.Models;
using Common.Security.Options;
using Common.Security.Services;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class OfflineAdministratorBootstrapTests
{
    [StudioPostgresFact]
    public async Task FreshPlanDoesNotCreateSchemaAndApplyPreservesBusinessPermissionsAndIsIdempotent()
    {
        await using var f = await Fixture.Create();
        var operation = f.Operation();
        var plan = operation.Plan(f.Request);
        Assert.True(plan.CreateAccount); Assert.True(plan.GrantAdministrator); Assert.False(f.SchemaExists());
        var frontDesk = f.Roles.GetRoles().Single(r => r.Name == StudioRoleNames.FrontDesk);
        f.Roles.SaveMember(frontDesk.Id, new(f.Request.LoginName, "Existing staff alias", true));
        var result = operation.Apply(f.Request, true);
        Assert.False(result.CreateAccount); Assert.False(result.ApproveAccount); Assert.False(result.GrantAdministrator);
        Assert.Single(f.Accounts.List()); Assert.Single(f.Accounts.GetIdentities(f.Request.AccountId));
        Assert.Single(f.Roles.GetMembers(frontDesk.Id));
        Assert.Equal(SecurityAccountStatus.Active, f.Accounts.Find(f.Request.AccountId)!.Status);
        Assert.Equal(new[] { "AccountAdministrativelyProvisioned", "ApplicationAuthorizationChanged", "AccountStatus:Active" }, f.Actions());
        long version = f.Accounts.Find(f.Request.AccountId)!.SessionVersion;
        operation.Apply(f.Request, true);
        Assert.Equal(version, f.Accounts.Find(f.Request.AccountId)!.SessionVersion); Assert.Equal(3, f.Actions().Length);
        using var services = new ServiceCollection().AddSingleton(f.Database).AddSingleton<IAccountStore>(f.Accounts)
            .AddSingleton(new AccountProvisioningService(f.Accounts)).BuildServiceProvider();
        var authenticated = new MockAuthenticator(f.Mock()).Authenticate(f.Request.LoginName, "");
        var principal = await StudioSignIn.CreatePrincipalAsync(authenticated.Identity!, services, default);
        Assert.True(principal!.IsInRole(StudioRoleNames.Administrator));
        Assert.Equal(f.Request.AccountId, principal.FindFirst(AccountSecurityHosting.AccountIdClaim)!.Value);
        Assert.False(new MockAuthenticator(f.Mock()).Authenticate("unlisted", "").IsAuthenticated);
    }

    [StudioPostgresFact]
    public async Task PendingAccessIsExplicitlyApprovedWithoutReplacingAccountOrBinding()
    {
        await using var f = await Fixture.Create(); f.Enroll();
        var before = f.Accounts.Find(f.Request.AccountId)!;
        var plan = f.Operation().Plan(f.Request); Assert.False(plan.CreateAccount); Assert.True(plan.ApproveAccount);
        f.Operation().Apply(f.Request, true);
        var after = f.Accounts.Find(f.Request.AccountId)!;
        Assert.Equal(before.Id, after.Id); Assert.Equal(before.LoginName, after.LoginName);
        Assert.Equal(SecurityAccountStatus.Active, after.Status); Assert.True(after.SessionVersion > before.SessionVersion);
        Assert.Single(f.Accounts.GetIdentities(after.Id));
        Assert.False(new AccountProvisioningService(f.Accounts).IsCurrentSession(before.Id, before.SessionVersion));
    }

    [StudioPostgresFact]
    public async Task ExistingNonGuidAccountIdentifierIsPreserved()
    {
        await using var f = await Fixture.Create(); f.Accounts.EnsureSchema();
        var request = f.Request with { AccountId = "preserved:staff-account" };
        f.Accounts.Create(new(request.AccountId, request.LoginName, "Existing staff", AuthenticationProviderKind.Mock,
            AccountPopulation.Staff, Identity: new("mock", "Aegis.Studio", request.SubjectId)), "fixture");
        var result = f.Operation().Apply(request, true);
        Assert.Equal(request.AccountId, result.AccountId);
        Assert.Equal(SecurityAccountStatus.Active, f.Accounts.Find(request.AccountId)!.Status);
        Assert.Single(f.Accounts.List());
    }

    [StudioPostgresFact]
    public async Task DisabledSuspendedRevokedAndConflictingAccountsAreNeverRelinkedOrReactivated()
    {
        await using var f = await Fixture.Create(); f.Enroll();
        foreach (var status in new[] { SecurityAccountStatus.Disabled, SecurityAccountStatus.Suspended })
        {
            f.Accounts.SetStatus(f.Request.AccountId, status, "fixture");
            Assert.Throws<InvalidOperationException>(() => f.Operation().Apply(f.Request, true));
            Assert.Equal(status, f.Accounts.Find(f.Request.AccountId)!.Status);
        }
        f.Accounts.SetStatus(f.Request.AccountId, SecurityAccountStatus.PendingAccess, "fixture");
        Assert.Throws<InvalidOperationException>(() => f.Operation().Plan(f.Request with { AccountId = Guid.NewGuid().ToString("D") }));
        var changed = f.Mock(subject: "different-subject");
        Assert.Throws<InvalidOperationException>(() => new OfflineAdministratorBootstrap(f.Database, changed).Plan(f.Request with { SubjectId = "different-subject" }));
        f.Accounts.Unlink(f.Request.AccountId, new("mock", "Aegis.Studio", f.Request.SubjectId), "fixture");
        Assert.Throws<InvalidOperationException>(() => f.Operation().Apply(f.Request, true));
        Assert.Empty(f.Accounts.GetIdentities(f.Request.AccountId));
        Assert.Empty(f.Administrators());
    }

    [StudioPostgresFact]
    public async Task IdentityBoundToAnotherAliasAndPopulationConflictRequireAnExplicitDecision()
    {
        await using var f = await Fixture.Create(); f.Accounts.EnsureSchema();
        f.Accounts.Create(new(f.Request.AccountId, "preserved-alias", "Staff", AuthenticationProviderKind.Mock, AccountPopulation.Staff,
            Identity: new("mock", "Aegis.Studio", f.Request.SubjectId)), "fixture");
        Assert.Throws<InvalidOperationException>(() => f.Operation().Apply(f.Request, true));
        Assert.Equal("preserved-alias", f.Accounts.Find(f.Request.AccountId)!.LoginName); Assert.Empty(f.Administrators());
    }

    [StudioPostgresFact]
    public async Task ProductionUnconfirmedIsolationUnlistedAndMismatchedSubjectsAreRejectedBeforeWrites()
    {
        await using var f = await Fixture.Create();
        foreach (var options in new[] { f.Mock(environment: "Production"), f.Mock(isolated: false), f.Mock(enabled: false), f.Mock(subject: "other") })
            Assert.Throws<InvalidOperationException>(() => new OfflineAdministratorBootstrap(f.Database, options).Apply(f.Request, true));
        Assert.Throws<InvalidOperationException>(() => f.Operation().Plan(f.Request with { LoginName = "unlisted" }));
        Assert.Throws<InvalidOperationException>(() => f.Operation().Apply(f.Request, false));
        Assert.False(f.SchemaExists()); Assert.Empty(f.Administrators());
    }

    [StudioPostgresFact]
    public async Task RemovedAdministratorMembershipIsNotSilentlyReactivated()
    {
        await using var f = await Fixture.Create(); f.Enroll();
        var role = f.Roles.GetRoles().Single(x => x.Name == StudioRoleNames.Administrator);
        f.Roles.SaveMember(role.Id, new(f.Request.LoginName, "Staff", false));
        Assert.Throws<InvalidOperationException>(() => f.Operation().Apply(f.Request, true));
        Assert.False(Assert.Single(f.Roles.GetMembers(role.Id)).IsActive);
        Assert.Equal(SecurityAccountStatus.PendingAccess, f.Accounts.Find(f.Request.AccountId)!.Status);
    }

    [StudioPostgresFact]
    public async Task AuditFailureRollsBackRoleAndVersionAndNeverApprovesPendingAccount()
    {
        await using var f = await Fixture.Create(); f.Enroll(); long version = f.Accounts.Find(f.Request.AccountId)!.SessionVersion;
        f.Sql("alter table common_security_account_events add constraint fail_bootstrap_audit check(action <> 'ApplicationAuthorizationChanged')");
        Assert.Throws<PostgresException>(() => f.Operation().Apply(f.Request, true));
        Assert.Empty(f.Administrators()); Assert.Equal(version, f.Accounts.Find(f.Request.AccountId)!.SessionVersion);
        Assert.Equal(SecurityAccountStatus.PendingAccess, f.Accounts.Find(f.Request.AccountId)!.Status);
        f.Sql("alter table common_security_account_events drop constraint fail_bootstrap_audit");
        f.Operation().Apply(f.Request, true); Assert.Single(f.Administrators());
    }

    [StudioPostgresFact]
    public async Task ApprovalAuditFailureLeavesAuditedPendingPreparationAndRetryCompletesSafely()
    {
        await using var f = await Fixture.Create(); f.Enroll();
        f.Sql("alter table common_security_account_events add constraint fail_approval_audit check(action <> 'AccountStatus:Active')");
        Assert.Throws<PostgresException>(() => f.Operation().Apply(f.Request, true));
        Assert.Equal(SecurityAccountStatus.PendingAccess, f.Accounts.Find(f.Request.AccountId)!.Status);
        Assert.Single(f.Administrators()); Assert.Contains("ApplicationAuthorizationChanged", f.Actions());
        f.Sql("alter table common_security_account_events drop constraint fail_approval_audit");
        f.Operation().Apply(f.Request, true);
        Assert.Equal(SecurityAccountStatus.Active, f.Accounts.Find(f.Request.AccountId)!.Status); Assert.Single(f.Administrators());
    }

    [StudioPostgresFact]
    public async Task AccountCreationAuditFailureLeavesNoAccountOrBinding()
    {
        await using var f = await Fixture.Create(); f.Accounts.EnsureSchema();
        f.Sql("alter table common_security_account_events add constraint fail_create_audit check(action <> 'AccountAdministrativelyProvisioned')");
        Assert.Throws<PostgresException>(() => f.Operation().Apply(f.Request, true));
        Assert.Empty(f.Accounts.List()); Assert.Empty(f.Accounts.GetIdentities(f.Request.AccountId)); Assert.Empty(f.Administrators());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string connection = Environment.GetEnvironmentVariable("STUDIO_TEST_POSTGRES")!;
        private readonly string schema = "bootstrap_" + Guid.NewGuid().ToString("N");
        private NpgsqlDataSource source = null!;
        public StudioDatabase Database { get; private set; } = null!;
        public GlobalOperationalRoleStore Roles { get; private set; } = null!;
        public RelationalAccountStore Accounts { get; private set; } = null!;
        public AdministratorBootstrapRequest Request { get; } = new("deployed.operator", "immutable-demo-subject", Guid.NewGuid().ToString("D"), "offline:test-operator");
        public MockAuthenticationOptions Mock(string environment = "Demo", bool isolated = true, bool enabled = true, string subject = "immutable-demo-subject") => new()
        { Enabled = enabled, EnvironmentName = environment, IsolationConfirmed = isolated, ApplicationId = "Aegis.Studio", Identities = [new(Request.LoginName, subject, "Demo Operator")] };
        public OfflineAdministratorBootstrap Operation() => new(Database, Mock());
        public void Enroll()
        {
            Accounts.EnsureSchema(); Accounts.Create(new(Request.AccountId, Request.LoginName, "Preserved Staff", AuthenticationProviderKind.Mock,
                AccountPopulation.Staff, Identity: new("mock", "Aegis.Studio", Request.SubjectId)), "fixture");
        }
        public static async Task<Fixture> Create()
        {
            var f = new Fixture(); await using var c = new NpgsqlConnection(f.connection); await c.OpenAsync();
            await using (var q = new NpgsqlCommand("create schema " + f.schema, c)) await q.ExecuteNonQueryAsync();
            f.source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(f.connection) { SearchPath = f.schema }.ConnectionString);
            f.Database = new(f.source); f.Roles = new(f.Database); f.Roles.EnsureSchema(); f.Accounts = new(() => f.source.CreateConnection(), "Aegis.Studio"); return f;
        }
        public bool SchemaExists() { using var c = Database.OpenConnection(); using var q = new NpgsqlCommand("select to_regclass('common_security_accounts') is not null", c); return (bool)q.ExecuteScalar()!; }
        public GlobalOperationalRoleMember[] Administrators() => Roles.GetMembers(Roles.GetRoles().Single(x => x.Name == StudioRoleNames.Administrator).Id).ToArray();
        public void Sql(string sql) { using var c = Database.OpenConnection(); using var q = new NpgsqlCommand(sql, c); q.ExecuteNonQuery(); }
        public string[] Actions() { using var c = Database.OpenConnection(); using var q = new NpgsqlCommand("select action from common_security_account_events order by occurred_at,event_id", c); using var r = q.ExecuteReader(); var actions = new List<string>(); while (r.Read()) actions.Add(r.GetString(0)); return actions.ToArray(); }
        public async ValueTask DisposeAsync() { await source.DisposeAsync(); await using var c = new NpgsqlConnection(connection); await c.OpenAsync(); await using var q = new NpgsqlCommand("drop schema " + schema + " cascade", c); await q.ExecuteNonQueryAsync(); }
    }
}
