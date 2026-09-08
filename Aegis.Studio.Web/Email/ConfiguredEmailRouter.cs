using Aegis.Studio.Application.EmailRouting;
using Aegis.Studio.Web.Operations;

namespace Aegis.Studio.Web.Email;

public sealed class ConfiguredEmailRouter : IInboundEmailRouter
{
    private const string RouteHeaderName = "X-Aegis-Route";
    private readonly WorkspaceStore workspaces;

    public ConfiguredEmailRouter(WorkspaceStore workspaces)
    {
        this.workspaces = workspaces;
    }

    public EmailRouteResult Route(InboundEmail email)
    {
        if (email.InternetHeaders.TryGetValue(RouteHeaderName, out string? routeKey) && !string.IsNullOrWhiteSpace(routeKey))
        {
            StudioWorkspaceDefinition? headerMatch = workspaces.FindByRouteKey(routeKey);
            if (headerMatch is not null)
            {
                return Routed(headerMatch);
            }
        }

        List<StudioWorkspaceDefinition> matches = email.ToAddresses
            .Concat(email.CcAddresses)
            .Select(NormalizeAddress)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(address => workspaces.FindByAddress(address))
            .Where(x => x is not null)
            .Cast<StudioWorkspaceDefinition>()
            .DistinctBy(x => x.Id)
            .ToList();

        if (matches.Count == 1)
        {
            return Routed(matches[0]);
        }

        if (matches.Count > 1)
        {
            return new EmailRouteResult(false, null, null, null, "Multiple persisted workspace routes matched this message. Manual review is required.");
        }

        return new EmailRouteResult(false, null, null, null, "No persisted workspace route matched To, Cc or X-Aegis-Route.");
    }

    private static string NormalizeAddress(string? value)
        => value?.Trim().Trim('<', '>').ToLowerInvariant() ?? string.Empty;

    private static EmailRouteResult Routed(StudioWorkspaceDefinition workspace)
        => new(true, workspace.Id, workspace.Name, workspace.EmailAddress, null);
}
