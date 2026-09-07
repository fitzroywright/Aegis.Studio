using System.Security.Claims;
using Aegis.Studio.Web.Persistence;

namespace Aegis.Studio.Web.Profiles;

public sealed class UserProfileStore
{
    private const string ProfilesCollection = "user-profiles";
    private readonly PostgresDocumentStore persistence;

    public UserProfileStore(PostgresDocumentStore persistence)
    {
        this.persistence = persistence;
    }

    public StudioUserProfile GetOrCreate(ClaimsPrincipal user)
    {
        string userName = GetUserName(user);
        Guid id = StableProfileId(userName);
        StudioUserProfile? existing = persistence.Get<StudioUserProfile>(ProfilesCollection, id);
        if (existing is not null)
        {
            return RefreshDirectoryIdentity(existing, user);
        }

        StudioUserProfile profile = new
        (
            id,
            userName,
            user.Identity?.Name ?? userName,
            user.FindFirstValue(ClaimTypes.Email),
            user.FindFirstValue("department"),
            user.FindFirstValue("title"),
            UserMessagingPreferences.Default,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow
        );
        persistence.Upsert(ProfilesCollection, id, profile);
        return profile;
    }

    public StudioUserProfile UpdatePreferences(ClaimsPrincipal user, UserMessagingPreferences preferences)
    {
        StudioUserProfile current = GetOrCreate(user);
        StudioUserProfile updated = current with
        {
            Messaging = Normalize(preferences),
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        persistence.Upsert(ProfilesCollection, current.Id, updated);
        return updated;
    }

    public IReadOnlyList<StudioUserProfile> GetAll()
        => persistence.GetAll<StudioUserProfile>(ProfilesCollection)
            .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public StudioUserProfile? UpdateAsAdministrator(string userName, UserMessagingPreferences preferences)
    {
        Guid id = StableProfileId(userName);
        StudioUserProfile? current = persistence.Get<StudioUserProfile>(ProfilesCollection, id);
        if (current is null)
        {
            return null;
        }

        StudioUserProfile updated = current with
        {
            Messaging = Normalize(preferences),
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        persistence.Upsert(ProfilesCollection, id, updated);
        return updated;
    }

    private StudioUserProfile RefreshDirectoryIdentity(StudioUserProfile profile, ClaimsPrincipal user)
    {
        StudioUserProfile refreshed = profile with
        {
            DisplayName = user.Identity?.Name ?? profile.DisplayName,
            Email = user.FindFirstValue(ClaimTypes.Email),
            Department = user.FindFirstValue("department"),
            Title = user.FindFirstValue("title"),
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        persistence.Upsert(ProfilesCollection, profile.Id, refreshed);
        return refreshed;
    }

    private static UserMessagingPreferences Normalize(UserMessagingPreferences value)
    {
        string deliveryMode = value.DeliveryMode?.Trim() switch
        {
            "DailyDigest" => "DailyDigest",
            _ => "Immediate"
        };

        string? quietStart = NormalizeClock(value.QuietHoursStart);
        string? quietEnd = NormalizeClock(value.QuietHoursEnd);
        return value with
        {
            DeliveryMode = deliveryMode,
            QuietHoursStart = quietStart,
            QuietHoursEnd = quietEnd
        };
    }

    private static string? NormalizeClock(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return TimeOnly.TryParse(value, out TimeOnly time)
            ? time.ToString("HH:mm")
            : null;
    }

    private static string GetUserName(ClaimsPrincipal user)
        => user.FindFirstValue("login_name")
            ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("Authenticated user has no login identifier.");

    private static Guid StableProfileId(string userName)
    {
        byte[] bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(userName.Trim().ToLowerInvariant()));
        return new Guid(bytes.AsSpan(0, 16));
    }
}

public sealed record StudioUserProfile
(
    Guid Id,
    string UserName,
    string DisplayName,
    string? Email,
    string? Department,
    string? Title,
    UserMessagingPreferences Messaging,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc
);

public sealed record UserMessagingPreferences
(
    bool EmailEnabled,
    bool InAppEnabled,
    bool SlackEnabled,
    bool TeamsEnabled,
    bool NewWorkspaceMessage,
    bool JobAssigned,
    bool BookingChanged,
    bool PaymentChanged,
    bool JobStatusChanged,
    bool MentionsAndReplies,
    string DeliveryMode,
    string? QuietHoursStart,
    string? QuietHoursEnd
)
{
    public static UserMessagingPreferences Default => new
    (
        true,
        true,
        false,
        false,
        true,
        true,
        true,
        true,
        true,
        true,
        "Immediate",
        null,
        null
    );
}
