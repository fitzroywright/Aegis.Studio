namespace Aegis.Studio.Application.EmailRouting;

public interface IInboundEmailRouter
{
    EmailRouteResult Route(InboundEmail email);
}
