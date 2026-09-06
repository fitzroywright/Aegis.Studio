using Aegis.Studio.Application.EmailRouting;

namespace Aegis.Studio.Web.Email;

public sealed class ConfiguredEmailRouter : IInboundEmailRouter
{
    private readonly IReadOnlyList<WorkspaceEmailRoute> routes;

    public ConfiguredEmailRouter(IConfiguration configuration)
    {
        routes = configuration
            .GetSection("EmailRouting:Routes")
            .Get<List<WorkspaceEmailRoute>>() ?? new List<WorkspaceEmailRoute>();
    }

    public EmailRouteResult Route(InboundEmail email)
    {
        foreach (string address in email.ToAddresses)
        {
            WorkspaceEmailRoute? route = routes.FirstOrDefault(x =>
                string.Equals(x.Address, address, StringComparison.OrdinalIgnoreCase));

            if (route is not null)
            {
                return new EmailRouteResult(
                    true,
                    route.WorkspaceId,
                    route.WorkspaceName,
                    route.Address,
                    null);
            }
        }

        return new EmailRouteResult(
            false,
            null,
            null,
            null,
            "No workspace route matched any recipient address.");
    }
}

public sealed class WorkspaceEmailRoute
{
    public Guid WorkspaceId { get; init; }

    public string WorkspaceName { get; init; } = string.Empty;

    public string Address { get; init; } = string.Empty;
}
