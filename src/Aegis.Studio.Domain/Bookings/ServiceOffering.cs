namespace Aegis.Studio.Domain.Bookings;

public sealed class ServiceOffering
{
    private ServiceOffering()
    {
    }

    public ServiceOffering(string name, int durationMinutes, int bufferMinutes)
    {
        Id = Guid.NewGuid();
        Name = name.Trim();
        DurationMinutes = durationMinutes;
        BufferMinutes = bufferMinutes;
        IsActive = true;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public int DurationMinutes { get; private set; }
    public int BufferMinutes { get; private set; }
    public bool IsActive { get; private set; }
}
