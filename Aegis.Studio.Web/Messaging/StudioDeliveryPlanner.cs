using Common.Messaging;

namespace Aegis.Studio.Web.Messaging;

public sealed record StudioDeliveryPlan(IReadOnlyList<string> ExternalRecipients, IReadOnlyList<StudioRoutingAttention> Attention,
    IReadOnlyList<StudioRoutingExclusion> Exclusions);
public sealed record StudioRoutingAttention(Guid DestinationId, string Reason);
public sealed record StudioRoutingExclusion(Guid DestinationId, string Recipient, string Reason);

public sealed class StudioDeliveryPlanner(IEmailRecipientDirectory directory, IConfiguration configuration)
{
    public async Task<StudioDeliveryPlan> PlanAsync(IReadOnlyList<StudioAddressEntry> destinations, CancellationToken token = default)
    {
        HashSet<string> external = new(StringComparer.OrdinalIgnoreCase);
        List<StudioRoutingAttention> attention = [];
        List<StudioRoutingExclusion> excluded = [];
        string monitor = StudioAddressBook.Normalize(configuration["Messaging:MicrosoftGraph:Email:SenderUpn"]);
        foreach (var destination in destinations)
        {
            if (destination.Status != "Active") { attention.Add(new(destination.Id, "Destination is inactive or requires attention.")); continue; }
            if (destination.DeliveryMode == "InternalOnly") continue;
            if (destination.StudioManagedGroup)
            {
                if (!configuration.GetValue("Messaging:Router:SelectiveFanOutEnabled", false) || !destination.GroupVerified)
                { attention.Add(new(destination.Id, "Selective fan-out awaits tenant verification.")); continue; }
                if (string.IsNullOrWhiteSpace(monitor)) { attention.Add(new(destination.Id, "The monitoring mailbox is not configured.")); continue; }
                var group = await directory.GetGroupAsync(destination.ProviderReference ?? "", token);
                if (!group.Supported || StudioAddressBook.Normalize(group.Address) != StudioAddressBook.Normalize(destination.EmailAddress))
                { attention.Add(new(destination.Id, group.AttentionReason ?? "The discovered group does not match the registered destination.")); continue; }
                foreach (var member in group.Members)
                {
                    string address;
                    try { address = StudioAddressBook.ValidateAddress(member); }
                    catch (InvalidOperationException) { attention.Add(new(destination.Id, "A group member address is invalid.")); break; }
                    if (address == monitor) excluded.Add(new(destination.Id, address, "Router monitoring mailbox excluded."));
                    else external.Add(address);
                }
            }
            else
            {
                if (destination.DeliveryMode == "Combined" && destination.CombinedMailboxPolicy == "Hold")
                { attention.Add(new(destination.Id, "Combined mailbox delivery requires review.")); continue; }
                if (destination.DeliveryMode == "Combined" && destination.CombinedMailboxPolicy == "InternalOnly") continue;
                if (StudioAddressBook.Normalize(destination.EmailAddress) == monitor)
                { attention.Add(new(destination.Id, "A monitoring mailbox cannot receive an outbound copy.")); continue; }
                external.Add(StudioAddressBook.ValidateAddress(destination.EmailAddress));
            }
        }
        // Do not send a partial recipient set when any destination cannot be resolved reliably.
        return new(attention.Count == 0 ? external.OrderBy(x => x).ToArray() : [], attention, excluded);
    }
}
