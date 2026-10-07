using Aegis.Studio.Application.EmailRouting;
using Aegis.Studio.Web.Messaging;
using Aegis.Studio.Web.Operations;

namespace Aegis.Studio.Web.Email;

public sealed class ConfiguredEmailRouter : IInboundEmailRouter
{
    private const string RouteHeaderName = "X-Aegis-Route";
    private readonly WorkspaceStore workspaces;
    private readonly StudioMessageAddressBook addresses;

    public ConfiguredEmailRouter(WorkspaceStore workspaces, StudioMessageAddressBook addresses)
    {
        this.workspaces = workspaces;
        this.addresses = addresses;
    }

    public EmailRouteResult Route(InboundEmail email)
    {
        EmailRouteResult? reply = addresses.ResolveReply(email);
        if (reply?.IsRouted == true) return reply;

        if (email.InternetHeaders.TryGetValue(RouteHeaderName, out string? routeKey) && !string.IsNullOrWhiteSpace(routeKey))
        {
            StudioMessageAddress? destination = addresses.ResolveRouteKey(routeKey);
            if (destination is not null) return Routed(destination);

            StudioWorkspaceDefinition? headerMatch = workspaces.FindByRouteKey(routeKey);
            if (headerMatch is not null) return Routed(headerMatch);
        }

        List<StudioMessageAddress> matches = email.ToAddresses
            .Concat(email.CcAddresses)
            .Select(NormalizeAddress)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(address => addresses.ResolveAddress(address))
            .Where(x => x is not null)
            .Cast<StudioMessageAddress>()
            .DistinctBy(x => $"{x.DestinationType}:{x.PersonUserName}:{x.JobId}:{x.WorkspaceId}", StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (matches.Count == 1) return Routed(matches[0]);

        if (matches.Count > 1)
        {
            return new EmailRouteResult(
                false, null, null, null,
                "Multiple Studio routes matched this message. Manual review is required.",
                "Ambiguous", null, null, "Aegis Studio");
        }

        return new EmailRouteResult(
            false, null, null, null,
            "No persisted Person, Workspace or Job route matched To, Cc, X-Aegis-Route, thread id or AEGIS reference.",
            "Unrouted", null, null, "Aegis Studio");
    }

    private static string NormalizeAddress(string? value)
        => value?.Trim().Trim('<', '>').ToLowerInvariant() ?? string.Empty;

    private static EmailRouteResult Routed(StudioWorkspaceDefinition workspace)
        => new(
            true,
            workspace.Id,
            workspace.Name,
            workspace.EmailAddress,
            null,
            "Workspace",
            workspace.Id,
            null,
            workspace.Name);

    private static EmailRouteResult Routed(StudioMessageAddress destination)
        => new(
            true,
            destination.WorkspaceId,
            destination.DestinationType == "Workspace" ? destination.DisplayName : null,
            destination.Address,
            null,
            destination.DestinationType,
            destination.JobId ?? destination.WorkspaceId,
            destination.PersonUserName,
            destination.DisplayName);
}
