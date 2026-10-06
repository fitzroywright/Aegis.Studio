using System.Security.Claims;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Profiles;
using Common.Security.Abstractions;
using Common.Security.Authorization;
using Common.Security.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Aegis.Studio.Web.Authentication;

public static class AuthenticationEndpoints
{
    public static void MapStudioAuthentication(this WebApplication app)
    {
        string[] administratorGroups = app.Configuration
            .GetSection("AuthenticationPlugin:Settings:AdministrativeGroups")
            .Get<string[]>() ?? [];
        bool mockAuthenticationEnabled = MockAuthenticationPolicy.IsEnabled(
            app.Configuration,
            app.Environment.EnvironmentName);

        app.MapGet("/api/auth/options", () => Results.Ok(new
        {
            mockAuthentication = mockAuthenticationEnabled,
            defaultUserName = mockAuthenticationEnabled
                ? app.Configuration["MockAuthentication:UserName"]?.Trim() ?? "studio.admin"
                : string.Empty
        })).AllowAnonymous();

        app.MapPost("/api/auth/login", async (
            LoginRequest request,
            IServiceProvider services,
            StudioDatabase database,
            HttpContext httpContext) =>
        {
            if (string.IsNullOrWhiteSpace(request.UserName))
            {
                return Results.BadRequest(new { error = "User name is required." });
            }

            string userName = request.UserName.Trim();
            string displayName;
            string? email;
            string? department;
            string? title;
            IReadOnlyCollection<string> groupNames;

            if (mockAuthenticationEnabled)
            {
                MockAuthenticationIdentity mockIdentity = MockAuthenticationPolicy.CreateIdentity(
                    app.Configuration,
                    userName,
                    administratorGroups);
                userName = mockIdentity.UserName;
                displayName = mockIdentity.DisplayName;
                email = mockIdentity.Email;
                department = mockIdentity.Department;
                title = mockIdentity.Title;
                groupNames = mockIdentity.GroupNames;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(request.Password))
                {
                    return Results.BadRequest(new { error = "User name and password are required." });
                }

                IAuthenticator authenticator = services.GetRequiredService<IAuthenticator>();
                AuthenticationResult result = authenticator.Authenticate(userName, request.Password);
                if (!result.IsAuthenticated)
                {
                    return Results.Unauthorized();
                }

                string? directoryDisplayName = result.UserInfo?.DisplayName;
                displayName = string.IsNullOrWhiteSpace(directoryDisplayName) ? userName : directoryDisplayName!;
                email = result.UserInfo?.Mail;
                department = result.UserInfo?.Department;
                title = result.UserInfo?.Title;
                groupNames = result.GroupNames;
            }

            GlobalOperationalRoleStore roles = new(database);
            roles.EnsureSchema(administratorGroups);
            roles.EnsureDefaultUserRole(userName, displayName);

            PermissionAuthorizer authorizer = new(roles);
            AuthorizationDecision roleDecision = await authorizer.AuthorizeAsync(
                AuthorizationSubject.Create(userName, groupNames),
                StudioPermissions.Access,
                httpContext.RequestAborted);

            List<Claim> claims =
            [
                new Claim(ClaimTypes.NameIdentifier, userName),
                new Claim(ClaimTypes.Name, displayName),
                new Claim("login_name", userName)
            ];
            AddOptionalClaim(claims, ClaimTypes.Email, email);
            AddOptionalClaim(claims, "department", department);
            AddOptionalClaim(claims, "title", title);

            foreach (string groupName in groupNames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                claims.Add(new Claim("directory_group", groupName));
            }

            foreach (string roleName in roleDecision.EffectiveRoles.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                claims.Add(new Claim(ClaimTypes.Role, roleName));
            }

            ClaimsIdentity identity = new(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            ClaimsPrincipal principal = new(identity);
            await httpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                principal,
                new AuthenticationProperties { IsPersistent = false, AllowRefresh = true });
            return Results.Ok(CreateCurrentUser(principal));
        }).AllowAnonymous();

        app.MapGet("/api/auth/me", (ClaimsPrincipal user, UserProfileStore profiles) =>
        {
            StudioUserProfile profile = profiles.GetOrCreate(user);
            return Results.Ok(new { user = CreateCurrentUser(user), profile });
        }).RequireAuthorization();

        app.MapPost("/api/auth/logout", async (HttpContext httpContext) =>
        {
            await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok(new { signedOut = true });
        }).RequireAuthorization();
    }

    private static object CreateCurrentUser(ClaimsPrincipal user)
        => new
        {
            userName = user.FindFirstValue("login_name") ?? user.Identity?.Name ?? string.Empty,
            displayName = user.Identity?.Name ?? string.Empty,
            email = user.FindFirstValue(ClaimTypes.Email),
            department = user.FindFirstValue("department"),
            title = user.FindFirstValue("title"),
            roles = user.FindAll(ClaimTypes.Role).Select(claim => claim.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            isAdministrator = user.IsInRole(StudioRoleNames.Administrator)
        };

    private static void AddOptionalClaim(List<Claim> claims, string claimType, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) claims.Add(new Claim(claimType, value));
    }
}

public sealed record LoginRequest(string UserName, string Password);
