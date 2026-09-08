using Aegis.Studio.Domain.Common;

namespace Aegis.Studio.Domain.Workspaces;

public sealed class Workspace : Entity
{
    public Workspace(Guid id, string name, string key) : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        Name = name.Trim();
        Key = key.Trim().ToLowerInvariant();
    }

    public string Name { get; }

    public string Key { get; }
}
