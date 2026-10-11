namespace Aegis.Studio.Web.Production;

public static class ProductionAuthorityRules
{
    public static bool IsProductionTask(string? kind) => kind is "Cull" or "Edit" or "Retouch" or "Proof" or "Print" or "QC" or "Production" or "Export" or "FileIngest" or "Backup" or "Archive";
    public static bool CanDispatch(bool productionManager, bool operationalManager, string? existingKind, string? requestedKind)
        => IsProductionTask(existingKind) || IsProductionTask(requestedKind) ? productionManager : productionManager || operationalManager;
    public static bool CanDecideQuality(bool productionManager, string? state) => state == "Pending" || productionManager;
    public static bool CanReport(bool productionManager, bool operationalManager, bool assigned, string? kind)
        => productionManager || assigned || (operationalManager && !IsProductionTask(kind));
}
