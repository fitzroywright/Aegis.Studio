namespace Aegis.Studio.Domain.Bookings;

// Preserve persisted numeric values: legacy Online=0, WhatsApp=3 and Manual=4.
public enum BookingSource
{
    Website = 0,
    WalkIn = 1,
    Phone = 2,
    Unknown = 3,
    StaffEntry = 4,
    Email = 5
}

public static class BookingSources
{
    public static IReadOnlyList<string> Selectable { get; } = Array.AsReadOnly(new[] { "Website", "Walk-In", "Phone", "Email", "Staff Entry" });
    public static string Normalize(string? source) => source?.Trim().ToLowerInvariant() switch
    {
        "website" or "web site" or "websitev1" or "online" => "Website",
        "walk-in" or "walkin" or "walk in" => "Walk-In",
        "phone" or "telephone" => "Phone",
        "email" => "Email",
        "staff entry" or "staffentry" or "manual" or "workday mobile" => "Staff Entry",
        _ => "Unknown"
    };
    public static string RequireSelectable(string source) => Selectable.Contains(source, StringComparer.Ordinal)
        ? source : throw new ArgumentException("Choose Website, Walk-In, Phone, Email or Staff Entry.");
    public static BookingSource Parse(string? source) => Normalize(source) switch
    {
        "Website" => BookingSource.Website, "Walk-In" => BookingSource.WalkIn,
        "Phone" => BookingSource.Phone, "Email" => BookingSource.Email,
        "Staff Entry" => BookingSource.StaffEntry, _ => BookingSource.Unknown
    };
}
