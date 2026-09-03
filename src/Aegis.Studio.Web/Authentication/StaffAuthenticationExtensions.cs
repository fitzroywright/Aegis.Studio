using System.Security.Claims;
using Common.Security.Abstractions;
using Common.Security.Models;
using Common.Security.Services.Functions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Aegis.Studio.Web.Authentication;

public static class StaffAuthenticationExtensions
{
    public static IServiceCollection AddStudioStaffAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "Aegis.Studio.Auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.LoginPath = "/login.html";
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;
                options.Events.OnRedirectToLogin = context =>
                {
                    if (context.Request.Path.StartsWithSegments("/api"))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    }

                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                };
            });
        services.AddAuthorization();
        return services;
    }

    public static WebApplication UseStudioStaffAuthentication(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
        app.Use(async (context, next) =>
        {
            if (!IsProtectedStaffPath(context.Request.Path) || context.User.Identity?.IsAuthenticated == true)
            {
                await next();
                return;
            }

            if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/diagnostics"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            string returnUrl = Uri.EscapeDataString(context.Request.Path + context.Request.QueryString);
            context.Response.Redirect($"/login.html?returnUrl={returnUrl}");
        });
        return app;
    }

    public static WebApplication MapStudioAuthenticationEndpoints(this WebApplication app)
    {
        app.MapPost("/api/auth/login", (Delegate)LoginAsync);
        app.MapPost("/api/auth/logout", (Delegate)LogoutAsync);
        app.MapGet("/api/auth/me", (ClaimsPrincipal user) => Results.Ok(new
        {
            authenticated = user.Identity?.IsAuthenticated == true,
            userName = user.Identity?.Name,
            displayName = user.FindFirst("display_name")?.Value
        })).RequireAuthorization();
        return app;
    }

    private static async Task<IResult> LoginAsync(LoginRequest request, IAuthenticator authenticator, HttpContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrEmpty(request.Password))
        {
            return Results.Json(new { error = "Windows username and password are required." }, statusCode: StatusCodes.Status401Unauthorized);
        }

        AuthenticationResult authentication = authenticator.Authenticate(request.UserName, request.Password);
        if (!authentication.IsAuthenticated)
        {
            return Results.Json(new { error = "The Windows username or password is incorrect, or Windows authentication is unavailable." }, statusCode: StatusCodes.Status401Unauthorized);
        }

        string userName = UserNameNormalizer.GetUserNameWithoutDomain(request.UserName);
        string displayName = authentication.UserInfo?.DisplayName ?? userName;
        List<Claim> claims =
        [
            new Claim(ClaimTypes.Name, userName),
            new Claim("windows_username", userName),
            new Claim("display_name", displayName)
        ];

        if (authentication.IsAdministrative)
        {
            claims.Add(new Claim(ClaimTypes.Role, "Administrator"));
        }

        ClaimsIdentity identity = new(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        ClaimsPrincipal principal = new(identity);
        AuthenticationProperties properties = new()
        {
            IsPersistent = request.RememberMe,
            AllowRefresh = true
        };
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, properties);
        return Results.Ok(new { userName, displayName });
    }

    private static async Task<IResult> LogoutAsync(HttpContext context)
    {
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.NoContent();
    }

    private static bool IsProtectedStaffPath(PathString path)
    {
        return path.StartsWithSegments("/api/workspaces")
            || path.StartsWithSegments("/api/conversations")
            || path.StartsWithSegments("/api/tasks")
            || path.StartsWithSegments("/api/alerts")
            || path.StartsWithSegments("/api/operations")
            || path.StartsWithSegments("/api/galleries")
            || path.StartsWithSegments("/api/media")
            || path.StartsWithSegments("/diagnostics")
            || path.StartsWithSegments("/workspaces.html")
            || path.StartsWithSegments("/operations.html")
            || path.StartsWithSegments("/galleries.html")
            || path.StartsWithSegments("/gallery.html");
    }
}

public sealed record LoginRequest(string UserName, string Password, bool RememberMe);
