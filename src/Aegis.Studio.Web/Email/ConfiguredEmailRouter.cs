using Aegis.Studio.Application.EmailRouting;

namespace Aegis.Studio.Web.Email;

public sealed class ConfiguredEmailRouter : IInboundEmailRouter
{
    private const string RouteHeaderName = "X-Aegis-Route";
    private readonly IReadOnlyList<WorkspaceEmailRoute> routes;

    public ConfiguredEmailRouter(IConfiguration configuration)
    {
        routes = configuration
            .GetSection("EmailRouting:Routes")
            .Get<List<WorkspaceEmailRoute>>() ?? new List<WorkspaceEmailRoute>();
    }

    public EmailRouteResult Route(InboundEmail email)
    {
        WorkspaceEmailRoute? headerRoute = ResolveHeaderRoute(email);
        if (headerRoute is not null)
        {
            return Routed(headerRoute);
        }

        List<WorkspaceEmailRoute> matches = email.ToAddresses
            .Concat(email.CcAddresses)
            .Select(NormalizeAddress)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(address => routes.Where(route =>
                route.IsEnabled &&
                string.Equals(NormalizeAddress(route.Address), address, StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(route => route.WorkspaceId)
            .ToList();

        if (matches.Count == 1)
        {
            return Routed(matches[0]);
        }

        if (matches.Count > 1)
        {
            return new EmailRouteResult(
                false,
                null,
                null,
                null,
                "Multiple workspace routes matched this message. Manual review is required.");
        }

        return new EmailRouteResult(
            false,
            null,
            null,
            null,
            "No workspace route matched To, Cc or X-Aegis-Route.");
    }

    private WorkspaceEmailRoute? ResolveHeaderRoute(InboundEmail email)
    {
        if (!email.InternetHeaders.TryGetValue(RouteHeaderName, out string? routeKey) ||
            string.IsNullOrWhiteSpace(routeKey))
        {
            return null;
        }

        string normalizedKey = routeKey.Trim();
        return routes.FirstOrDefault(route =>
            route.IsEnabled &&
            !string.IsNullOrWhiteSpace(route.RouteKey) &&
            string.Equals(route.RouteKey.Trim(), normalizedKey, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeAddress(string? value)
    {
        return value?.Trim().Trim('<', '>').ToLowerInvariant() ?? string.Empty;
    }

    private static EmailRouteResult Routed(WorkspaceEmailRoute route)
    {
        return new EmailRouteResult(
            true,
            route.WorkspaceId,
            route.WorkspaceName,
            route.Address,
            null);
    }
}

public sealed class WorkspaceEmailRoute
{
    public Guid WorkspaceId { get; init; }

    public string WorkspaceName { get; init; } = string.Empty;

    public string Address { get; init; } = string.Empty;

    public string RouteKey { get; init; } = string.Empty;

    public bool IsEnabled { get; init; } = true;
}
