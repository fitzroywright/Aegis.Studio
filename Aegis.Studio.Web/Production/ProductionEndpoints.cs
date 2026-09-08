namespace Aegis.Studio.Web.Production;

public static class ProductionEndpoints
{
    public static void MapStudioProduction(this WebApplication app)
    {
        app.MapStudioOperationalWork();
    }
}
