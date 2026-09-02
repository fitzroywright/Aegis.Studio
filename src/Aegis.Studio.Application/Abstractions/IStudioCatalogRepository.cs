using Aegis.Studio.Domain.Bookings;

namespace Aegis.Studio.Application.Abstractions;

public interface IStudioCatalogRepository
{
    Task<ServiceOffering?> GetServiceAsync(Guid id, CancellationToken cancellationToken);
    Task<Photographer?> GetPhotographerAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<ServiceOffering>> GetServicesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<Photographer>> GetPhotographersAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<AvailabilityRule>> GetAvailabilityRulesAsync(Guid photographerId, DayOfWeek dayOfWeek, CancellationToken cancellationToken);
}
