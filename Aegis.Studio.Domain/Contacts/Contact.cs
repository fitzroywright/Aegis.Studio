using Aegis.Studio.Domain.Common;

namespace Aegis.Studio.Domain.Contacts;

public sealed class Contact : Entity
{
    public Contact(Guid id, string displayName, string? email = null, string? phone = null) : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        DisplayName = displayName.Trim();
        Email = Normalize(email);
        Phone = Normalize(phone);
    }

    public string DisplayName { get; }

    public string? Email { get; }

    public string? Phone { get; }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
