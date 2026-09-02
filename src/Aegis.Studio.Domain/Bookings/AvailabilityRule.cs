namespace Aegis.Studio.Domain.Bookings;

public sealed class AvailabilityRule
{
    private AvailabilityRule()
    {
    }

    public AvailabilityRule(Guid photographerId, DayOfWeek dayOfWeek, TimeOnly startLocal, TimeOnly endLocal)
    {
        Id = Guid.NewGuid();
        PhotographerId = photographerId;
        DayOfWeek = dayOfWeek;
        StartLocal = startLocal;
        EndLocal = endLocal;
    }

    public Guid Id { get; private set; }
    public Guid PhotographerId { get; private set; }
    public DayOfWeek DayOfWeek { get; private set; }
    public TimeOnly StartLocal { get; private set; }
    public TimeOnly EndLocal { get; private set; }
}
