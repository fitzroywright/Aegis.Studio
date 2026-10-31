using System.Security.Claims;
using Aegis.Studio.Web.Operations;
using Common.Security.Abstractions;
using Common.Security.Authenticators;
using Common.Security.Hosting;
using Common.Security.Models;
using Common.Security.Options;
using Common.Security.Services;

namespace Aegis.Studio.Web.Authentication;

/// <summary>Studio-owned demo enrollment using the shared audited account and identity store.</summary>
public static class DemoStaffAuthentication
{
    public static bool CanManage(MockAuthenticationOptions options, string provider) =>
        provider == "Mock" && options.ApplicationId == "Aegis.Studio" &&
        options.EnvironmentName.Equals("Demo", StringComparison.OrdinalIgnoreCase) &&
        MockEnvironmentPolicy.IsPermitted(options.Enabled, options.EnvironmentName, options.IsolationConfirmed);

    private static bool IsDemoIdentity(LinkedAuthenticationIdentity identity, MockAuthenticationOptions options) =>
        identity.ProviderKind == AuthenticationProviderKind.Mock && identity.Key.ProviderId == "mock" &&
        identity.Key.ScopeId == options.ApplicationId;

    public static AuthenticationResult Authenticate(MockAuthenticationOptions options, IAccountStore accounts, string loginName)
    {
        if (!MockEnvironmentPolicy.IsPermitted(options.Enabled, options.EnvironmentName, options.IsolationConfirmed))
            return AuthenticationResult.Failed(AuthenticationFailure.Prohibited);
        // Existing operator-owned definitions retain their immutable subjects and behavior.
        if (options.Identities.Any(i => string.Equals(i.LoginName, loginName.Trim(), StringComparison.OrdinalIgnoreCase)))
            return new MockAuthenticator(options).Authenticate(loginName, string.Empty);
        if (!CanManage(options, "Mock")) return AuthenticationResult.Failed();
        var account = accounts.List().SingleOrDefault(a => a.Population == AccountPopulation.Staff &&
            string.Equals(a.LoginName, loginName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (account is null || account.Status is SecurityAccountStatus.Disabled or SecurityAccountStatus.Suspended)
            return AuthenticationResult.Failed();
        var identities = accounts.GetIdentities(account.Id).Where(i => IsDemoIdentity(i, options)).ToArray();
        if (identities.Length != 1) return AuthenticationResult.Failed();
        // The allowlist is the administrator-enrolled binding, never the requested alias alone.
        var managed = new MockAuthenticationOptions
        {
            Enabled = options.Enabled, IsolationConfirmed = options.IsolationConfirmed,
            EnvironmentName = options.EnvironmentName, ApplicationId = options.ApplicationId,
            Password = string.Empty,
            Identities = [new(account.LoginName, identities[0].Key.SubjectId, account.DisplayName, [])]
        };
        return new MockAuthenticator(managed).Authenticate(loginName, string.Empty);
    }

    public static SecurityAccount Enroll(MockAuthenticationOptions options, string provider, IAccountStore accounts,
        DemoStaffAccountRequest request, string actorId)
    {
        if (!CanManage(options, provider)) throw new InvalidOperationException("Demo enrollment requires isolated Demo sign-in.");
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UserName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        string login = request.UserName.Trim(), display = request.DisplayName.Trim();
        var configured = options.Identities.SingleOrDefault(i => string.Equals(i.LoginName, login, StringComparison.OrdinalIgnoreCase));
        if (request.AccountId is not null)
        {
            var account = accounts.Find(request.AccountId) ?? throw new InvalidOperationException("The selected staff account no longer exists.");
            if (account.Population != AccountPopulation.Staff || !string.Equals(account.LoginName, login, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Choose the existing staff account explicitly; its login must match.");
            if (account.Status is SecurityAccountStatus.Disabled or SecurityAccountStatus.Suspended)
                throw new InvalidOperationException("Review this account's disabled or suspended status before enrollment.");
            var identities = accounts.GetIdentities(account.Id).Where(i => IsDemoIdentity(i, options)).ToArray();
            if (identities.Length > 1 || (identities.Length == 1 && configured is not null && identities[0].Key.SubjectId != configured.SubjectId))
                throw new InvalidOperationException("The configured demo identity conflicts with this account's existing binding.");
            if (identities.Length == 1) return account;
            var key = new ExternalIdentityKey("mock", options.ApplicationId, configured?.SubjectId ?? Guid.NewGuid().ToString("D"));
            accounts.Link(account.Id, new VerifiedIdentity(key, AuthenticationProviderKind.Mock, account.LoginName,
                account.DisplayName, DateTimeOffset.UtcNow, "administrator-approved-demo-binding"), actorId);
            return accounts.Find(account.Id)!;
        }
        // Creation is pending, with no application role grants. The store enforces alias/identity uniqueness.
        return accounts.Create(new AccountCreation(Guid.NewGuid().ToString("D"), login, display,
            AuthenticationProviderKind.Mock, AccountPopulation.Staff, SecurityAccountStatus.PendingAccess,
            new ExternalIdentityKey("mock", options.ApplicationId, configured?.SubjectId ?? Guid.NewGuid().ToString("D"))), actorId);
    }

    public static void MapDemoStaffAccounts(this WebApplication app, MockAuthenticationOptions options, string provider)
    {
        var group = app.MapGroup("/api/admin/security/demo-accounts").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (!CanManage(options, provider)) return Results.NotFound();
            if (!BrowserMutationPolicy.IsAllowed(http.Request)) return Results.StatusCode(403);
            string? actor = http.User.FindFirstValue(AccountSecurityHosting.AccountIdClaim);
            bool current = actor is not null && long.TryParse(http.User.FindFirstValue(AccountSecurityHosting.SessionVersionClaim), out long version) &&
                http.RequestServices.GetRequiredService<AccountProvisioningService>().IsCurrentSession(actor, version);
            return current && http.User.IsInRole(StudioRoleNames.Administrator) ? await next(context) : Results.StatusCode(403);
        });
        group.MapPost("/", (DemoStaffAccountRequest request, IAccountStore accounts, ClaimsPrincipal actor) =>
        {
            try
            {
                return Results.Ok(Enroll(options, provider, accounts, request,
                    actor.FindFirstValue(AccountSecurityHosting.AccountIdClaim)!));
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = e.Message });
            }
        });
    }
}

public sealed record DemoStaffAccountRequest(string UserName, string DisplayName, string? AccountId = null);
