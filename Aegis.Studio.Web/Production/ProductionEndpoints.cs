using Aegis.Studio.Web.Versioning;

namespace Aegis.Studio.Web.Production;

public static class ProductionEndpoints
{
    public static void MapStudioProduction(this WebApplication app)
    {
        app.MapStudioOperationalWork();
        app.MapStudioPhotographerMobile();
        app.MapStudioVersioning();
    }
}
