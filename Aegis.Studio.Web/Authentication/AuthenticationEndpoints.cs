using System.Security.Claims;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Profiles;
using Common.Security.Abstractions;
using Common.Security.Authenticators;
using Common.Security.Authorization;
using Common.Security.Hosting;
using Common.Security.Integration;
using Common.Security.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Aegis.Studio.Web.Authentication;

public static class AuthenticationEndpoints
{
    public static void MapStudioAuthentication(this WebApplication app)
    {
        var mock = StudioAuthenticationProvider.ReadMockOptions(app.Configuration.GetSection("MockAuthentication"),
            app.Environment.EnvironmentName, "Aegis.Studio");
        bool mockEnabled = MockEnvironmentPolicy.IsPermitted(mock.Enabled, mock.EnvironmentName, mock.IsolationConfirmed);
        if (mock.Enabled && !mockEnabled) throw new InvalidOperationException("Mock authentication requires an explicitly isolated Development or Demo environment.");
        bool entraEnabled = app.Configuration.GetValue("Authentication:Entra:Enabled", false);
        string provider = StudioAuthenticationProvider.Resolve(app.Configuration, mockEnabled, entraEnabled);
        app.MapGet("/api/auth/options", () => Results.Ok(new
        {
            defaultUserName = provider == "Mock" ? mock.Identities.FirstOrDefault()?.LoginName ?? "" : "",
            defaultSource = provider,
            demoAccountsEnabled = DemoStaffAuthentication.CanManage(mock, provider)
        })).AllowAnonymous();

        app.MapPost("/api/auth/login", async (LoginRequest request, IServiceProvider services, HttpContext context) =>
        {
            if (string.IsNullOrWhiteSpace(request.UserName)) return Results.BadRequest(new { error = "User name is required." });
            if (provider == "EntraId" || !StudioAuthenticationProvider.AcceptsSource(provider, request.Source))
                return Results.BadRequest(new { error = "Use the configured sign-in service." });
            AuthenticationResult result = provider switch
            {
                "ActiveDirectory" => services.GetService<IAuthenticator>()?.Authenticate(request.UserName, request.Password)
                    ?? AuthenticationResult.Failed(AuthenticationFailure.ProviderUnavailable),
                "LocalDatabase" => new LocalDatabaseAuthenticator(services.GetRequiredService<ILocalCredentialStore>()).Authenticate(request.UserName, request.Password),
                "Mock" => DemoStaffAuthentication.Authenticate(mock, services.GetRequiredService<IAccountStore>(), request.UserName),
                _ => AuthenticationResult.Failed(AuthenticationFailure.Prohibited)
            };
            if (!result.IsAuthenticated) return Results.Unauthorized();
            try
            {
                ClaimsPrincipal? principal = await StudioSignIn.CreatePrincipalAsync(result.Identity!, services, context.RequestAborted, result.UserInfo);
                if (principal is null) return Results.Json(new { error = "This account has no approved Studio access." }, statusCode: 403);
                await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
                    new AuthenticationProperties { IsPersistent = false, AllowRefresh = true });
                return Results.Ok(CreateCurrentUser(principal));
            }
            catch (IdentityEnrollmentRequiredException)
            {
                return Results.Json(new { error = "An administrator must approve this account's provider identity binding.", accountState = "PendingIdentityLink" }, statusCode: 403);
            }
        }).AllowAnonymous().RequireRateLimiting(AccountSecurityHosting.LoginRateLimit);

        app.MapGet("/api/auth/entra", () => provider == "EntraId"
            ? Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }, ["StudioEntra"])
            : Results.NotFound()).AllowAnonymous().RequireRateLimiting(AccountSecurityHosting.LoginRateLimit);

        app.MapGet("/api/auth/me", (ClaimsPrincipal user, UserProfileStore profiles, IAccountStore accounts) =>
        {
            var account = accounts.Find(user.FindFirstValue(AccountSecurityHosting.AccountIdClaim) ?? "");
            StudioUserProfile profile = profiles.GetOrCreate(user);
            return Results.Ok(new { user = CreateCurrentUser(user), profile, accountState = account?.Status.ToString() });
        }).RequireAuthorization();
        app.MapPost("/api/auth/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok(new { signedOut = true });
        }).RequireAuthorization();
        app.MapDemoStaffAccounts(mock, provider);
        app.MapCommonAccountAdministration("/api/admin/security/accounts", AccountPopulation.Staff,
            new Dictionary<AuthenticationProviderKind, string>
            {
                [AuthenticationProviderKind.ActiveDirectory] = "active-directory",
                [AuthenticationProviderKind.EntraId] = "studio-entra",
                [AuthenticationProviderKind.LocalDatabase] = "local"
            }, context => context.User.IsInRole(StudioRoleNames.Administrator), "/api/auth/recover");
    }

    private static object CreateCurrentUser(ClaimsPrincipal user) => new
    {
        userName = user.FindFirstValue("login_name") ?? user.Identity?.Name ?? "",
        displayName = user.Identity?.Name ?? "", email = user.FindFirstValue(ClaimTypes.Email),
        department = user.FindFirstValue("department"), title = user.FindFirstValue("title"),
        roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
        isAdministrator = user.IsInRole(StudioRoleNames.Administrator),
        authenticationSource = user.FindFirstValue("authentication_source")
        ,accountState = user.FindFirstValue("account_state")
    };
}

public static class StudioSignIn
{
    public static Task RedirectAccessDeniedAsync(RedirectContext<CookieAuthenticationOptions> context)
    {
        if (context.Request.Path.StartsWithSegments("/api")) context.Response.StatusCode = StatusCodes.Status403Forbidden;
        else context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    }

    public static async Task ValidateCookieAsync(CookieValidatePrincipalContext context)
    {
        await AccountSecurityHosting.ValidateCookieAsync(context);
        if (context.Principal is null) return;
        var services = context.HttpContext.RequestServices;
        var account = services.GetRequiredService<IAccountStore>().Find(context.Principal.FindFirstValue(AccountSecurityHosting.AccountIdClaim)!);
        if (account?.Status != SecurityAccountStatus.Active) return;
        var store = new GlobalOperationalRoleStore(services.GetRequiredService<StudioDatabase>());
        var decision = await new PermissionAuthorizer(store).AuthorizeAsync(AuthorizationSubject.Create(account.LoginName,
            context.Principal.FindAll("directory_group").Select(x => x.Value).ToArray()), StudioPermissions.Access, context.HttpContext.RequestAborted);
        if (!decision.Allowed)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(context.Scheme.Name);
            return;
        }
        string[] current = context.Principal.FindAll(ClaimTypes.Role).Select(x => x.Value).Order(StringComparer.Ordinal).ToArray();
        string[] expected = decision.EffectiveRoles.Order(StringComparer.Ordinal).ToArray();
        if (!current.SequenceEqual(expected, StringComparer.Ordinal))
        {
            var claims = context.Principal.Claims.Where(c => c.Type != ClaimTypes.Role).Concat(expected.Select(r => new Claim(ClaimTypes.Role, r)));
            context.ReplacePrincipal(new ClaimsPrincipal(new ClaimsIdentity(claims, context.Scheme.Name)));
            context.ShouldRenew = true;
        }
    }

    public static async Task<ClaimsPrincipal?> CreatePrincipalAsync(VerifiedIdentity identity, IServiceProvider services,
        CancellationToken cancellationToken, ApplicationUserInfo? profile = null)
    {
        var provisioning = services.GetRequiredService<Common.Security.Services.AccountProvisioningService>();
        var account = provisioning.Provision(identity, AccountPopulation.Staff).Account;
        if (account.Status is SecurityAccountStatus.Disabled or SecurityAccountStatus.Suspended) return null;
        List<Claim> claims = [new(ClaimTypes.NameIdentifier, account.LoginName), new(ClaimTypes.Name, account.DisplayName), new("login_name", account.LoginName)];
        claims.AddRange(AccountSecurityHosting.SessionClaims(account, identity));
        if (!string.IsNullOrWhiteSpace(profile?.Mail)) claims.Add(new(ClaimTypes.Email, profile.Mail));
        if (!string.IsNullOrWhiteSpace(profile?.Department)) claims.Add(new("department", profile.Department));
        if (!string.IsNullOrWhiteSpace(profile?.Title)) claims.Add(new("title", profile.Title));
        if (account.Status == SecurityAccountStatus.Active)
        {
            var roles = new GlobalOperationalRoleStore(services.GetRequiredService<StudioDatabase>());
            var decision = await new PermissionAuthorizer(roles).AuthorizeAsync(
                AuthorizationSubject.Create(account.LoginName, identity.GroupNames), StudioPermissions.Access, cancellationToken);
            if (!decision.Allowed) return null;
            claims.AddRange(identity.GroupNames.Select(g => new Claim("directory_group", g)));
            claims.AddRange(decision.EffectiveRoles.Select(r => new Claim(ClaimTypes.Role, r)));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }
}

public sealed record LoginRequest(string UserName, string Password = "", string? Source = null);

