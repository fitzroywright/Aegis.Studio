using Aegis.Studio.Web.Versioning;
using Aegis.Studio.Web.WorkDay;

namespace Aegis.Studio.Web.Production;

public static class ProductionEndpoints
{
    public static void MapStudioProduction(this WebApplication app)
    {
        app.MapProductionConfiguration();
        app.MapStudioOperationalWork();
        app.MapStudioWorkDay();
        app.MapStudioVersioning();
    }
}
