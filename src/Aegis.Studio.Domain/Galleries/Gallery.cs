namespace Aegis.Studio.Domain.Galleries;

public sealed class Gallery
{
    private Gallery() { }
    public Gallery(string name, GalleryKind kind, Guid workspaceId, Guid? bookingId, Guid? customerId) { Id = Guid.NewGuid(); Name = name.Trim(); Kind = kind; WorkspaceId = workspaceId; BookingId = bookingId; CustomerId = customerId; CreatedUtc = DateTimeOffset.UtcNow; }
    public Guid Id { get; private set; } public string Name { get; private set; } = string.Empty; public GalleryKind Kind { get; private set; } public Guid WorkspaceId { get; private set; } public Guid? BookingId { get; private set; } public Guid? CustomerId { get; private set; } public DateTimeOffset CreatedUtc { get; private set; }
}
