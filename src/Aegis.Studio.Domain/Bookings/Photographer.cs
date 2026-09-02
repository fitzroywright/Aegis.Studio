namespace Aegis.Studio.Domain.Bookings;

public sealed class Photographer
{
    private Photographer()
    {
    }

    public Photographer(string name, string notificationEmail)
    {
        Id = Guid.NewGuid();
        Name = name.Trim();
        NotificationEmail = notificationEmail.Trim();
        IsActive = true;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string NotificationEmail { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }
}
