using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Aegis.Studio.Web.Authentication;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Common.Security.Abstractions;
using Common.Security.Hosting;
using Common.Security.Integration;
using Common.Security.Models;
using Common.Security.Options;
using Common.Security.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class DemoStaffAuthenticationTests
{
    private static MockAuthenticationOptions Options(string environment = "Demo", bool enabled = true,
        bool isolated = true, IReadOnlyList<MockIdentityDefinition>? identities = null) => new()
    {
        ApplicationId = "Aegis.Studio", EnvironmentName = environment,
        Enabled = enabled, IsolationConfirmed = isolated, Password = string.Empty, Identities = identities ?? []
    };

    [Theory]
    [InlineData("Production", true, true, "Mock")]
    [InlineData("Staging", true, true, "Mock")]
    [InlineData("Development", true, true, "Mock")]
    [InlineData("Demo", false, true, "Mock")]
    [InlineData("Demo", true, false, "Mock")]
    [InlineData("Demo", true, true, "ActiveDirectory")]
    [InlineData("Demo", true, true, "LocalDatabase")]
    [InlineData("Demo", true, true, "EntraId")]
    public void EnrollmentOutsideSelectedIsolatedDemoIsRejectedBeforeAnyAccountWrite(string environment,
        bool enabled, bool isolated, string provider)
    {
        var options = Options(environment, enabled, isolated);
        Assert.False(DemoStaffAuthentication.CanManage(options, provider));
        Assert.Throws<InvalidOperationException>(() => DemoStaffAuthentication.Enroll(options, provider, null!,
            new("demo.jason", "Jason Brown"), "administrator"));
    }

    [StudioPostgresFact]
    public async Task NewDemoStaffSignsInWithoutPasswordButRequiresSeparateApprovalAndRoles()
    {
        await using var f = await Fixture.Create();
        var account = DemoStaffAuthentication.Enroll(Options(), "Mock", f.Accounts, new("demo.jason", "Jason Brown"), "operator");
        Assert.Equal(SecurityAccountStatus.PendingAccess, account.Status);
        Assert.Equal(AuthenticationProviderKind.Mock, account.ProviderKind);
        var stored = Assert.Single(f.Accounts.GetIdentities(account.Id));
        Assert.True(Guid.TryParse(stored.Key.SubjectId, out _));
        var proof = DemoStaffAuthentication.Authenticate(Options(), f.Accounts, "DEMO.JASON");
        Assert.True(proof.IsAuthenticated);
        Assert.Equal(stored.Key, proof.Identity!.Key);
        using var services = f.Services();
        var pending = await StudioSignIn.CreatePrincipalAsync(proof.Identity!, services, default);
        Assert.NotNull(pending); Assert.Empty(pending.FindAll(ClaimTypes.Role));
        Assert.Equal("PendingAccess", pending.FindFirstValue("account_state"));
        f.Accounts.SetStatus(account.Id, SecurityAccountStatus.Active, "operator");
        Assert.Null(await StudioSignIn.CreatePrincipalAsync(proof.Identity!, services, default));
        var userRole = f.Roles.GetRoles().Single(r => r.Name == StudioRoleNames.User);
        f.Roles.SaveMember(userRole.Id, new(account.LoginName, account.DisplayName));
        var approved = await StudioSignIn.CreatePrincipalAsync(proof.Identity!, services, default);
        Assert.True(approved!.IsInRole(StudioRoleNames.User));
        Assert.False(approved.IsInRole(StudioRoleNames.Administrator));
        Assert.Equal(account.Id, approved.FindFirstValue(AccountSecurityHosting.AccountIdClaim));
        Assert.False(DemoStaffAuthentication.Authenticate(Options(), f.Accounts, "unlisted").IsAuthenticated);
    }

    [StudioPostgresFact]
    public async Task ExplicitExistingAccountBindingPreservesIdsOtherIdentitiesAndStatusAndRetriesAreIdempotent()
    {
        await using var f = await Fixture.Create();
        var ad = new ExternalIdentityKey("active-directory", Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"));
        var original = f.Accounts.Create(new("preserved-account-id", "demo.jason", "Jason Brown",
            AuthenticationProviderKind.ActiveDirectory, AccountPopulation.Staff, Identity: ad), "operator");
        var request = new DemoStaffAccountRequest(original.LoginName, original.DisplayName, original.Id);
        var enrolled = DemoStaffAuthentication.Enroll(Options(), "Mock", f.Accounts, request, "operator");
        Assert.Equal(original.Id, enrolled.Id); Assert.Equal(original.Status, enrolled.Status);
        Assert.Equal(original.ProviderKind, enrolled.ProviderKind);
        Assert.Contains(f.Accounts.GetIdentities(original.Id), i => i.Key == ad);
        Assert.Equal(2, f.Accounts.GetIdentities(original.Id).Count);
        Assert.True(enrolled.SessionVersion > original.SessionVersion);
        Assert.False(new AccountProvisioningService(f.Accounts).IsCurrentSession(original.Id, original.SessionVersion, false));
        var subject = f.Accounts.GetIdentities(original.Id).Single(i => i.ProviderKind == AuthenticationProviderKind.Mock).Key.SubjectId;
        var again = DemoStaffAuthentication.Enroll(Options(), "Mock", f.Accounts, request, "operator");
        Assert.Equal(enrolled.SessionVersion, again.SessionVersion); Assert.Single(f.Accounts.List());
        Assert.Equal(subject, f.Accounts.GetIdentities(original.Id).Single(i => i.ProviderKind == AuthenticationProviderKind.Mock).Key.SubjectId);
        Assert.Throws<InvalidOperationException>(() => DemoStaffAuthentication.Enroll(Options(), "Mock", f.Accounts,
            request with { UserName = "another-user" }, "operator"));
        Assert.Throws<IdentityEnrollmentRequiredException>(() => DemoStaffAuthentication.Enroll(Options(), "Mock", f.Accounts,
            new(original.LoginName, original.DisplayName), "operator"));
    }

    [StudioPostgresFact]
    public async Task ConfigurationSubjectsArePreservedAndConflictingBindingsAreNotReplaced()
    {
        await using var f = await Fixture.Create();
        string subject = Guid.NewGuid().ToString("D");
        var options = Options(identities: [new("demo.jason", subject, "Jason Brown", [])]);
        var account = DemoStaffAuthentication.Enroll(options, "Mock", f.Accounts, new("demo.jason", "Jason Brown"), "operator");
        Assert.Equal(subject, Assert.Single(f.Accounts.GetIdentities(account.Id)).Key.SubjectId);
        Assert.Equal(subject, DemoStaffAuthentication.Authenticate(options, f.Accounts, account.LoginName).Identity!.Key.SubjectId);
        Assert.Equal(subject, DemoStaffAuthentication.Authenticate(Options(), f.Accounts, account.LoginName).Identity!.Key.SubjectId);
        var conflicting = Options(identities: [new("demo.jason", Guid.NewGuid().ToString("D"), "Jason Brown", [])]);
        Assert.Throws<InvalidOperationException>(() => DemoStaffAuthentication.Enroll(conflicting, "Mock", f.Accounts,
            new(account.LoginName, account.DisplayName, account.Id), "operator"));
        Assert.Equal(subject, Assert.Single(f.Accounts.GetIdentities(account.Id)).Key.SubjectId);
    }

    [StudioPostgresFact]
    public async Task DisabledSuspendedAndUnlinkedIdentitiesCannotAuthenticateAndNoStatusIsReactivated()
    {
        await using var f = await Fixture.Create();
        var account = DemoStaffAuthentication.Enroll(Options(), "Mock", f.Accounts, new("demo.jason", "Jason Brown"), "operator");
        foreach (var status in new[] { SecurityAccountStatus.Disabled, SecurityAccountStatus.Suspended })
        {
            f.Accounts.SetStatus(account.Id, status, "operator");
            Assert.False(DemoStaffAuthentication.Authenticate(Options(), f.Accounts, account.LoginName).IsAuthenticated);
            Assert.Throws<InvalidOperationException>(() => DemoStaffAuthentication.Enroll(Options(), "Mock", f.Accounts,
                new(account.LoginName, account.DisplayName, account.Id), "operator"));
            Assert.Equal(status, f.Accounts.Find(account.Id)!.Status);
        }
        f.Accounts.SetStatus(account.Id, SecurityAccountStatus.PendingAccess, "operator");
        var binding = Assert.Single(f.Accounts.GetIdentities(account.Id));
        f.Accounts.Unlink(account.Id, binding.Key, "operator");
        Assert.False(DemoStaffAuthentication.Authenticate(Options(), f.Accounts, account.LoginName).IsAuthenticated);
        Assert.Empty(f.Accounts.GetIdentities(account.Id)); Assert.Single(f.Accounts.List());
        Assert.False(DemoStaffAuthentication.Authenticate(Options("Production"), f.Accounts, account.LoginName).IsAuthenticated);
        Assert.False(DemoStaffAuthentication.Authenticate(Options(isolated: false), f.Accounts, account.LoginName).IsAuthenticated);
    }

    [StudioPostgresFact]
    public async Task EnrollmentEndpointRequiresCurrentAdministratorAndRejectsCrossOriginMutations()
    {
        await using var f = await Fixture.Create();
        var admin = DemoStaffAuthentication.Enroll(Options(), "Mock", f.Accounts, new("operator", "Operator"), "fixture");
        f.Accounts.SetStatus(admin.Id, SecurityAccountStatus.Active, "fixture");
        admin = f.Accounts.Find(admin.Id)!;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IAccountStore>(f.Accounts);
        builder.Services.AddSingleton(new AccountProvisioningService(f.Accounts));
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
            o.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; });
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.Use(async (http, next) =>
        {
            string fixture = http.Request.Headers["X-Test-Identity"].ToString();
            if (fixture.Length > 0)
                http.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(AccountSecurityHosting.AccountIdClaim, admin.Id),
                    new Claim(AccountSecurityHosting.SessionVersionClaim, (fixture == "stale" ? admin.SessionVersion - 1 : admin.SessionVersion).ToString()),
                    new Claim(ClaimTypes.Role, fixture == "staff" ? StudioRoleNames.User : StudioRoleNames.Administrator)
                }, "fixture"));
            await next();
        });
        app.UseAuthorization();
        app.MapDemoStaffAccounts(Options(), "Mock");
        await app.StartAsync();
        try
        {
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(app.Urls.Single()) };
            var request = new DemoStaffAccountRequest("demo.jason", "Jason Brown");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/admin/security/demo-accounts/", request)).StatusCode);
            foreach (string identity in new[] { "staff", "stale" })
            {
                client.DefaultRequestHeaders.Remove("X-Test-Identity"); client.DefaultRequestHeaders.Add("X-Test-Identity", identity);
                Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/admin/security/demo-accounts/", request)).StatusCode);
            }
            client.DefaultRequestHeaders.Remove("X-Test-Identity"); client.DefaultRequestHeaders.Add("X-Test-Identity", "admin");
            client.DefaultRequestHeaders.Add("Origin", "https://another.example");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/admin/security/demo-accounts/", request)).StatusCode);
            Assert.Single(f.Accounts.List());
            client.DefaultRequestHeaders.Remove("Origin");
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/admin/security/demo-accounts/", request)).StatusCode);
            Assert.Equal(2, f.Accounts.List().Count);
            var jason = f.Accounts.List().Single(a => a.LoginName == request.UserName);
            Assert.Equal(SecurityAccountStatus.PendingAccess, jason.Status);
        }
        finally { await app.StopAsync(); }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly NpgsqlConnection admin;
        private readonly string schema;
        private readonly NpgsqlDataSource source;
        public StudioDatabase Database { get; }
        public RelationalAccountStore Accounts { get; }
        public GlobalOperationalRoleStore Roles { get; }
        private Fixture(NpgsqlConnection admin, string schema, NpgsqlDataSource source)
        {
            this.admin = admin; this.schema = schema; this.source = source;
            Database = new(source); Accounts = new(() => source.CreateConnection(), "Aegis.Studio");
            Accounts.EnsureSchema(); Roles = new(Database); Roles.EnsureSchema();
        }
        public static async Task<Fixture> Create()
        {
            string connection = Environment.GetEnvironmentVariable("STUDIO_TEST_POSTGRES")!;
            var admin = new NpgsqlConnection(connection); await admin.OpenAsync();
            string schema = "demo_staff_" + Guid.NewGuid().ToString("N");
            await using (var command = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin)) await command.ExecuteNonQueryAsync();
            return new(admin, schema, NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(connection) { SearchPath = schema }.ConnectionString));
        }
        public ServiceProvider Services() => new ServiceCollection().AddSingleton(Database).AddSingleton<IAccountStore>(Accounts)
            .AddSingleton(new AccountProvisioningService(Accounts)).BuildServiceProvider();
        public async ValueTask DisposeAsync()
        {
            await source.DisposeAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", admin);
            await command.ExecuteNonQueryAsync(); await admin.DisposeAsync();
        }
    }
}
