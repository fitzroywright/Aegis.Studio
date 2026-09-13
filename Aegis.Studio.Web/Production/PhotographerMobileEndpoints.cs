using System.Security.Claims;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Scheduling;

namespace Aegis.Studio.Web.Production;

public static class PhotographerMobileEndpoints
{
    public static void MapStudioPhotographerMobile(this WebApplication app)
    {
        RouteGroupBuilder mobile = app.MapGroup("/api/mobile/photographer").RequireAuthorization();

        mobile.MapGet("/me", (ClaimsPrincipal user, StudioDatabase database) =>
        {
            PhotographerStore photographers = new(database);
            string userName = GetUserName(user);
            PhotographerProfile? profile = photographers.Get(userName);
            return profile is null ? Results.NotFound(new { error = "No photographer profile is linked to this account." }) : Results.Ok(profile);
        });

        mobile.MapGet("/schedule", (
            ClaimsPrincipal user,
            DateTimeOffset? from,
            DateTimeOffset? to,
            StudioDatabase database,
            ScheduleStore schedules) =>
        {
            PhotographerStore photographers = new(database);
            string userName = GetUserName(user);
            PhotographerProfile? profile = photographers.Get(userName);
            if (profile is null) return Results.NotFound(new { error = "No photographer profile is linked to this account." });

            DateTimeOffset start = from?.ToUniversalTime() ?? DateTimeOffset.UtcNow.AddDays(-1);
            DateTimeOffset end = to?.ToUniversalTime() ?? DateTimeOffset.UtcNow.AddDays(60);

            IReadOnlyList<ScheduleEntry> entries = schedules.GetEntries(start, end)
                .Where(x => x.ResourceIds.Contains(profile.ScheduleResourceId))
                .OrderBy(x => x.StartsAtUtc)
                .ToArray();

            return Results.Ok(entries);
        });

        mobile.MapGet("/availability", (
            ClaimsPrincipal user,
            DateTimeOffset? from,
            DateTimeOffset? to,
            StudioDatabase database) =>
        {
            PhotographerStore photographers = new(database);
            string userName = GetUserName(user);
            if (photographers.Get(userName) is null) return Results.NotFound(new { error = "No photographer profile is linked to this account." });
            return Results.Ok(photographers.GetAvailability(userName, from, to));
        });

        mobile.MapPost("/availability", (
            ClaimsPrincipal user,
            PhotographerAvailabilityRequest request,
            StudioDatabase database) =>
        {
            PhotographerStore photographers = new(database);
            string userName = GetUserName(user);
            try
            {
                return Results.Created("/api/mobile/photographer/availability", photographers.AddAvailability(userName, request));
            }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "No photographer profile is linked to this account." }); }
            catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
        });
    }

    private static string GetUserName(ClaimsPrincipal user)
        => user.FindFirstValue("login_name") ?? user.Identity?.Name ?? string.Empty;
}
