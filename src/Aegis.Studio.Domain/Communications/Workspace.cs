namespace Aegis.Studio.Domain.Communications;

public sealed class Workspace
{
    private Workspace()
    {
    }

    public Workspace(string name, string slug, string publicEmailAddress)
    {
        Id = Guid.NewGuid();
        Name = name.Trim();
        Slug = slug.Trim().ToLowerInvariant();
        PublicEmailAddress = publicEmailAddress.Trim().ToLowerInvariant();
        IsActive = true;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Slug { get; private set; } = string.Empty;
    public string PublicEmailAddress { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }
}
