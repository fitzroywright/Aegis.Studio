using System.Security.Claims;
using Aegis.Pos.Core;
using Aegis.Studio.Web.Diagnostics;
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
        services.AddSingleton<IAccountingGateway, NullAccountingGateway>();
        services.AddSingleton<AccountingStore>();
        services.AddStudioEngineeringDiagnostics();
        return services;
    }

    public static void MapStudioPosHardening(this WebApplication app)
    {
        app.MapStudioEngineeringDiagnostics();

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

        RouteGroupBuilder register = app.MapGroup("/api/pos/register").RequireAuthorization();
        register.MapPost("/open", (OpenRegisterRequest request, ClaimsPrincipal user, AccountingStore accounting) =>
        {
            try
            {
                string cashier = user.Identity?.Name ?? "Aegis.Studio";
                return Results.Ok(accounting.OpenRegister(request.TerminalId, cashier, request.OpeningFloat));
            }
            catch (Exception exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });
        register.MapGet("/open/{terminalId}", (string terminalId, AccountingStore accounting) =>
            accounting.GetOpenRegister(terminalId) is RegisterSession session ? Results.Ok(session) : Results.NotFound());
        register.MapGet("/{sessionId:guid}/reconciliation", (Guid sessionId, AccountingStore accounting) =>
        {
            try { return Results.Ok(accounting.Reconcile(sessionId)); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
        });
        register.MapPost("/{sessionId:guid}/close", (Guid sessionId, CloseRegisterRequest request, ClaimsPrincipal user, AccountingStore accounting) =>
        {
            try
            {
                return Results.Ok(accounting.CloseRegister(sessionId, request.CountedCash, user.Identity?.Name ?? "Aegis.Studio", request.Note));
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
        });

        RouteGroupBuilder accountingAdmin = app.MapGroup("/api/admin/accounting")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "Administrator" });
        accountingAdmin.MapGet("/mappings", (AccountingStore accounting) => Results.Ok(accounting.GetMappings()));
        accountingAdmin.MapPut("/mappings", (AccountMapping mapping, AccountingStore accounting) =>
        {
            try { return Results.Ok(accounting.SaveMapping(mapping)); }
            catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
        });
        accountingAdmin.MapPost("/batches/register/{sessionId:guid}", (Guid sessionId, AccountingStore accounting) =>
        {
            try { return Results.Ok(accounting.GenerateBatch(sessionId)); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
        });
        accountingAdmin.MapGet("/batches/{batchId:guid}", (Guid batchId, AccountingStore accounting) =>
            accounting.GetBatch(batchId) is AccountingBatch batch ? Results.Ok(batch) : Results.NotFound());
        accountingAdmin.MapPost("/batches/{batchId:guid}/export", async (Guid batchId, AccountingStore accounting, CancellationToken cancellationToken) =>
        {
            try
            {
                AccountingExportResult result = await accounting.ExportBatchAsync(batchId, cancellationToken);
                return result.Success ? Results.Ok(result) : Results.Conflict(result);
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (Exception exception) { return Results.BadRequest(new { error = exception.Message }); }
        });
    }
}
