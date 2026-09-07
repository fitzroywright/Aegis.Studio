using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace Aegis.Studio.Web.Profiles;

public static class UserProfileEndpoints
{
    public static void MapStudioUserProfiles(this WebApplication app)
    {
        app.MapGet("/api/profile", (ClaimsPrincipal user, UserProfileStore profiles) =>
            Results.Ok(profiles.GetOrCreate(user)))
            .RequireAuthorization();

        app.MapPut("/api/profile/messaging", (ClaimsPrincipal user, UserMessagingPreferences request, UserProfileStore profiles) =>
            Results.Ok(profiles.UpdatePreferences(user, request)))
            .RequireAuthorization();

        RouteGroupBuilder administration = app.MapGroup("/api/admin/users")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "Administrator" });

        administration.MapGet("/profiles", (UserProfileStore profiles) =>
            Results.Ok(profiles.GetAll()));

        administration.MapPut("/{userName}/messaging", (string userName, UserMessagingPreferences request, UserProfileStore profiles) =>
        {
            StudioUserProfile? updated = profiles.UpdateAsAdministrator(userName, request);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        });
    }
}
