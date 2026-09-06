namespace Aegis.Studio.Domain.Jobs;

public enum JobStatus
{
    New = 1,
    Scheduled = 2,
    Shooting = 3,
    InProduction = 4,
    WaitingOnClient = 5,
    ReadyForDelivery = 6,
    Closed = 7,
    Cancelled = 8
}
