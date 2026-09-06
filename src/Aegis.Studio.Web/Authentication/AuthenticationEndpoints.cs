using System.Security.Claims;
using Common.Security.Abstractions;
using Common.Security.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Aegis.Studio.Web.Authentication;

public static class AuthenticationEndpoints
{
    public static void MapStudioAuthentication(this WebApplication app)
    {
        app.MapPost("/api/auth/login", async (
            LoginRequest request,
            IAuthenticator authenticator,
            HttpContext httpContext) =>
        {
            if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.Password))
            {
                return Results.BadRequest(new { error = "User name and password are required." });
            }

            AuthenticationResult result = authenticator.Authenticate(request.UserName.Trim(), request.Password);
            if (!result.IsAuthenticated)
            {
                return Results.Unauthorized();
            }

            string userName = request.UserName.Trim();
            string? directoryDisplayName = result.UserInfo?.DisplayName;
            string displayName = string.IsNullOrWhiteSpace(directoryDisplayName)
                ? userName
                : directoryDisplayName!;

            List<Claim> claims =
            [
                new Claim(ClaimTypes.NameIdentifier, userName),
                new Claim(ClaimTypes.Name, displayName),
                new Claim("login_name", userName)
            ];

            AddOptionalClaim(claims, ClaimTypes.Email, result.UserInfo?.Mail);
            AddOptionalClaim(claims, "department", result.UserInfo?.Department);
            AddOptionalClaim(claims, "title", result.UserInfo?.Title);

            if (result.IsAdministrative)
            {
                claims.Add(new Claim(ClaimTypes.Role, "Administrator"));
            }

            ClaimsIdentity identity = new(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            ClaimsPrincipal principal = new(identity);
            AuthenticationProperties properties = new()
            {
                IsPersistent = false,
                AllowRefresh = true
            };

            await httpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                principal,
                properties);

            return Results.Ok(CreateCurrentUser(principal));
        }).AllowAnonymous();

        app.MapGet("/api/auth/me", (ClaimsPrincipal user) =>
            Results.Ok(CreateCurrentUser(user)))
            .RequireAuthorization();

        app.MapPost("/api/auth/logout", async (HttpContext httpContext) =>
        {
            await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok(new { signedOut = true });
        }).RequireAuthorization();
    }

    private static object CreateCurrentUser(ClaimsPrincipal user)
    {
        return new
        {
            userName = user.FindFirstValue("login_name") ?? user.Identity?.Name ?? string.Empty,
            displayName = user.Identity?.Name ?? string.Empty,
            email = user.FindFirstValue(ClaimTypes.Email),
            department = user.FindFirstValue("department"),
            title = user.FindFirstValue("title"),
            isAdministrator = user.IsInRole("Administrator")
        };
    }

    private static void AddOptionalClaim(List<Claim> claims, string claimType, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            claims.Add(new Claim(claimType, value));
        }
    }
}

public sealed record LoginRequest(string UserName, string Password);
