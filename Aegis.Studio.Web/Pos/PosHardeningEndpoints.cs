using System.Security.Claims;
using Aegis.Pos.Core;
using Aegis.Studio.Web.Operations;
using Microsoft.AspNetCore.Authorization;

namespace Aegis.Studio.Web.Pos;

public static class PosHardeningEndpoints
{
    public static IServiceCollection AddStudioPosHardening(this IServiceCollection services)
    {
        services.AddSingleton<PosAuditStore>();
        services.AddSingleton<PosCatalogStore>();
        services.AddSingleton<PosReceiptStore>();
        return services;
    }

    public static void MapStudioPosHardening(this WebApplication app)
    {
        app.MapGet("/api/pos/catalog", (PosCatalogStore catalog) => Results.Ok(catalog.GetAll()));

        RouteGroupBuilder catalogAdmin = app.MapGroup("/api/admin/pos/catalog")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "Administrator" });
        catalogAdmin.MapGet("/", (PosCatalogStore catalog) => Results.Ok(catalog.GetAll(true)));
        catalogAdmin.MapPost("/", (PosCatalogItemRequest request, PosCatalogStore catalog) =>
        {
            try
            {
                PosCatalogItem item = catalog.Save(request);
                return Results.Created($"/api/admin/pos/catalog/{item.Id}", item);
            }
            catch (Exception exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });
        catalogAdmin.MapPut("/{id:guid}", (Guid id, PosCatalogItemRequest request, PosCatalogStore catalog) =>
        {
            if (catalog.Get(id) is null)
            {
                return Results.NotFound();
            }
            try
            {
                return Results.Ok(catalog.Save(request, id));
            }
            catch (Exception exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        app.MapGet("/api/pos/diagnostics", (StudioPosStore pos, PosAuditStore audit) => Results.Ok(new
        {
            sales = pos.GetDiagnostics(),
            recentAudit = audit.GetRecent(25)
        }));

        app.MapGet("/api/pos/audit", (int? count, PosAuditStore audit) =>
            Results.Ok(audit.GetRecent(count ?? 100)))
            .RequireAuthorization(new AuthorizeAttribute { Roles = "Administrator" });

        app.MapGet("/api/pos/sales/{saleId:guid}/receipt", (Guid saleId, StudioPosStore pos, StudioStore studio, PosReceiptStore receipts) =>
        {
            Sale? sale = pos.Get(saleId);
            if (sale is null || sale.Status != SaleStatus.Completed)
            {
                return Results.NotFound();
            }
            if (!Guid.TryParse(sale.ReferenceId, out Guid jobId) || studio.GetJob(jobId) is not StudioJob job)
            {
                return Results.NotFound();
            }
            PosReceiptDocument receipt = receipts.GetOrCreate(sale, job, sale.CashierUserId ?? "Aegis.Studio");
            return Results.Ok(receipt);
        });

        app.MapGet("/api/pos/sales/{saleId:guid}/receipt/text", (Guid saleId, StudioPosStore pos, StudioStore studio, PosReceiptStore receipts) =>
        {
            Sale? sale = pos.Get(saleId);
            if (sale is null || sale.Status != SaleStatus.Completed)
            {
                return Results.NotFound();
            }
            if (!Guid.TryParse(sale.ReferenceId, out Guid jobId) || studio.GetJob(jobId) is not StudioJob job)
            {
                return Results.NotFound();
            }
            PosReceiptDocument receipt = receipts.GetOrCreate(sale, job, sale.CashierUserId ?? "Aegis.Studio");
            return Results.Text(receipts.RenderText(receipt), "text/plain; charset=utf-8");
        });

        app.MapPost("/api/pos/sales/{saleId:guid}/refunds/hardened", (Guid saleId, RefundStudioSaleRequest request, ClaimsPrincipal user, StudioStore studio, StudioPosStore pos) =>
        {
            if (pos.Get(saleId) is null)
            {
                return Results.NotFound();
            }
            try
            {
                return Results.Ok(pos.Refund(saleId, request, studio, user.Identity?.Name ?? "Aegis.Studio"));
            }
            catch (Exception exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });
    }
}
