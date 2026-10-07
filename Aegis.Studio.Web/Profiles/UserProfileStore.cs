using System.Security.Claims;
using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Profiles;

public sealed class UserProfileStore
{
    private static readonly HashSet<string> AllowedThemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "system",
        "light",
        "dark"
    };

    private readonly StudioDatabase database;

    public UserProfileStore(StudioDatabase database)
    {
        this.database = database;
    }

    public StudioUserProfile GetOrCreate(ClaimsPrincipal user)
    {
        string userName = GetUserName(user);
        StudioUserProfile? existing = GetByUserName(userName);
        if (existing is not null)
        {
            return RefreshDirectoryIdentity(existing, user);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        StudioUserProfile profile = new(StableProfileId(userName), userName, user.Identity?.Name ?? userName, user.FindFirstValue(ClaimTypes.Email), user.FindFirstValue("department"), user.FindFirstValue("title"), "system", UserMessagingPreferences.Default, now, now);
        Save(profile);
        return profile;
    }

    public StudioUserProfile UpdatePreferences(ClaimsPrincipal user, UserMessagingPreferences preferences)
    {
        StudioUserProfile current = GetOrCreate(user);
        StudioUserProfile updated = current with { Messaging = Normalize(preferences), UpdatedAtUtc = DateTimeOffset.UtcNow };
        Save(updated);
        return updated;
    }

    public StudioUserProfile UpdateTheme(ClaimsPrincipal user, string themeName)
    {
        StudioUserProfile current = GetOrCreate(user);
        string normalized = NormalizeTheme(themeName);
        StudioUserProfile updated = current with { ThemeName = normalized, UpdatedAtUtc = DateTimeOffset.UtcNow };
        Save(updated);
        return updated;
    }

    public IReadOnlyList<StudioUserProfile> GetAll()
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = SelectSql + " order by display_name";
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<StudioUserProfile> values = [];
        while (reader.Read()) values.Add(Read(reader));
        return values;
    }

    public StudioUserProfile? UpdateAsAdministrator(string userName, UserMessagingPreferences preferences)
    {
        StudioUserProfile? current = GetByUserName(userName);
        if (current is null) return null;
        StudioUserProfile updated = current with { Messaging = Normalize(preferences), UpdatedAtUtc = DateTimeOffset.UtcNow };
        Save(updated);
        return updated;
    }

    private StudioUserProfile? GetByUserName(string userName)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = SelectSql + " where lower(user_name)=lower($1)";
        command.Parameters.AddWithValue(userName.Trim());
        using NpgsqlDataReader reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private void Save(StudioUserProfile profile)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            insert into studio_user_profiles
            (id,user_name,display_name,email,department,title,theme_name,email_enabled,in_app_enabled,slack_enabled,teams_enabled,whatsapp_enabled,whatsapp_mobile,new_workspace_message,job_assigned,booking_changed,payment_changed,job_status_changed,mentions_and_replies,delivery_mode,quiet_hours_start,quiet_hours_end,created_at_utc,updated_at_utc)
            values ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21,$22,$23,$24)
            on conflict(id) do update set
            user_name=excluded.user_name,display_name=excluded.display_name,email=excluded.email,department=excluded.department,title=excluded.title,theme_name=excluded.theme_name,
            email_enabled=excluded.email_enabled,in_app_enabled=excluded.in_app_enabled,slack_enabled=excluded.slack_enabled,teams_enabled=excluded.teams_enabled,whatsapp_enabled=excluded.whatsapp_enabled,whatsapp_mobile=excluded.whatsapp_mobile,
            new_workspace_message=excluded.new_workspace_message,job_assigned=excluded.job_assigned,booking_changed=excluded.booking_changed,payment_changed=excluded.payment_changed,
            job_status_changed=excluded.job_status_changed,mentions_and_replies=excluded.mentions_and_replies,delivery_mode=excluded.delivery_mode,
            quiet_hours_start=excluded.quiet_hours_start,quiet_hours_end=excluded.quiet_hours_end,updated_at_utc=excluded.updated_at_utc
            """;
        UserMessagingPreferences m = profile.Messaging;
        object? quietStart = TimeOnly.TryParse(m.QuietHoursStart, out TimeOnly start) ? start : DBNull.Value;
        object? quietEnd = TimeOnly.TryParse(m.QuietHoursEnd, out TimeOnly end) ? end : DBNull.Value;
        object?[] values = [profile.Id, profile.UserName, profile.DisplayName, (object?)profile.Email ?? DBNull.Value, (object?)profile.Department ?? DBNull.Value, (object?)profile.Title ?? DBNull.Value, NormalizeTheme(profile.ThemeName), m.EmailEnabled, m.InAppEnabled, m.SlackEnabled, m.TeamsEnabled, m.WhatsAppEnabled, (object?)m.WhatsAppMobile ?? DBNull.Value, m.NewWorkspaceMessage, m.JobAssigned, m.BookingChanged, m.PaymentChanged, m.JobStatusChanged, m.MentionsAndReplies, m.DeliveryMode, quietStart, quietEnd, profile.CreatedAtUtc, profile.UpdatedAtUtc];
        for (int index = 0; index < values.Length; index++) command.Parameters.AddWithValue(values[index] ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private StudioUserProfile RefreshDirectoryIdentity(StudioUserProfile profile, ClaimsPrincipal user)
    {
        StudioUserProfile refreshed = profile with { DisplayName = user.Identity?.Name ?? profile.DisplayName, Email = user.FindFirstValue(ClaimTypes.Email), Department = user.FindFirstValue("department"), Title = user.FindFirstValue("title"), UpdatedAtUtc = DateTimeOffset.UtcNow };
        Save(refreshed);
        return refreshed;
    }

    private static StudioUserProfile Read(NpgsqlDataReader r)
    {
        string? quietStart = r.IsDBNull(20) ? null : r.GetFieldValue<TimeOnly>(20).ToString("HH:mm");
        string? quietEnd = r.IsDBNull(21) ? null : r.GetFieldValue<TimeOnly>(21).ToString("HH:mm");
        UserMessagingPreferences messaging = new(
            EmailEnabled: r.GetBoolean(7),
            InAppEnabled: r.GetBoolean(8),
            SlackEnabled: r.GetBoolean(9),
            TeamsEnabled: r.GetBoolean(10),
            WhatsAppEnabled: r.GetBoolean(11),
            WhatsAppMobile: r.IsDBNull(12) ? null : r.GetString(12),
            NewWorkspaceMessage: r.GetBoolean(13),
            JobAssigned: r.GetBoolean(14),
            BookingChanged: r.GetBoolean(15),
            PaymentChanged: r.GetBoolean(16),
            JobStatusChanged: r.GetBoolean(17),
            MentionsAndReplies: r.GetBoolean(18),
            DeliveryMode: r.GetString(19),
            QuietHoursStart: quietStart,
            QuietHoursEnd: quietEnd);
        return new StudioUserProfile(r.GetGuid(0),r.GetString(1),r.GetString(2),r.IsDBNull(3)?null:r.GetString(3),r.IsDBNull(4)?null:r.GetString(4),r.IsDBNull(5)?null:r.GetString(5),NormalizeTheme(r.IsDBNull(6)?null:r.GetString(6)),messaging,r.GetFieldValue<DateTimeOffset>(22),r.GetFieldValue<DateTimeOffset>(23));
    }

    private static UserMessagingPreferences Normalize(UserMessagingPreferences value)
    {
        string deliveryMode = value.DeliveryMode?.Trim() == "DailyDigest" ? "DailyDigest" : "Immediate";
        return value with { DeliveryMode = deliveryMode, QuietHoursStart = NormalizeClock(value.QuietHoursStart), QuietHoursEnd = NormalizeClock(value.QuietHoursEnd) };
    }

    private static string NormalizeTheme(string? value)
    {
        string theme = string.IsNullOrWhiteSpace(value) ? "system" : value.Trim().ToLowerInvariant();
        theme = theme switch
        {
            "office-light" => "light",
            "fluent-dark" => "dark",
            "studio-default" or "mid-century" or "steel" or "sakura" or "ocean" => "system",
            _ => theme
        };
        return AllowedThemes.Contains(theme) ? theme : "system";
    }

    private static string? NormalizeClock(string? value) => string.IsNullOrWhiteSpace(value) ? null : TimeOnly.TryParse(value, out TimeOnly time) ? time.ToString("HH:mm") : null;
    private static string GetUserName(ClaimsPrincipal user) => user.FindFirstValue("login_name") ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("Authenticated user has no login identifier.");
    private static Guid StableProfileId(string userName) { byte[] bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(userName.Trim().ToLowerInvariant())); return new Guid(bytes.AsSpan(0,16)); }

    private const string SelectSql = "select id,user_name,display_name,email,department,title,theme_name,email_enabled,in_app_enabled,slack_enabled,teams_enabled,whatsapp_enabled,whatsapp_mobile,new_workspace_message,job_assigned,booking_changed,payment_changed,job_status_changed,mentions_and_replies,delivery_mode,quiet_hours_start,quiet_hours_end,created_at_utc,updated_at_utc from studio_user_profiles";
}

public sealed record StudioUserProfile(Guid Id,string UserName,string DisplayName,string? Email,string? Department,string? Title,string ThemeName,UserMessagingPreferences Messaging,DateTimeOffset CreatedAtUtc,DateTimeOffset UpdatedAtUtc);
public sealed record UserMessagingPreferences(bool EmailEnabled,bool InAppEnabled,bool SlackEnabled,bool TeamsEnabled,bool WhatsAppEnabled,string? WhatsAppMobile,bool NewWorkspaceMessage,bool JobAssigned,bool BookingChanged,bool PaymentChanged,bool JobStatusChanged,bool MentionsAndReplies,string DeliveryMode,string? QuietHoursStart,string? QuietHoursEnd)
{
    public static UserMessagingPreferences Default => new(
        EmailEnabled: true,
        InAppEnabled: true,
        SlackEnabled: false,
        TeamsEnabled: false,
        WhatsAppEnabled: false,
        WhatsAppMobile: null,
        NewWorkspaceMessage: true,
        JobAssigned: true,
        BookingChanged: true,
        PaymentChanged: true,
        JobStatusChanged: true,
        MentionsAndReplies: true,
        DeliveryMode: "Immediate",
        QuietHoursStart: null,
        QuietHoursEnd: null);
}

public sealed record UpdateThemeRequest(string ThemeName);
