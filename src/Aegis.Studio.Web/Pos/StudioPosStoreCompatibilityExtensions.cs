using Aegis.Pos.Core;
using Aegis.Studio.Web.Operations;

namespace Aegis.Studio.Web.Pos;

public static class StudioPosStoreCompatibilityExtensions
{
    public static Sale Create(this StudioPosStore store, Guid jobId, CreateStudioSaleRequest request, StudioStore studio)
        => store.Create(jobId, request, studio, "Aegis.Studio");

    public static Sale Refund(this StudioPosStore store, Guid saleId, RefundStudioSaleRequest request, StudioStore studio)
        => store.Refund(saleId, request, studio, "Aegis.Studio");
}
