using Common.Storage;

namespace Aegis.Studio.Web.Storage;

public sealed class StorageFailureMiddleware(RequestDelegate next, ILogger<StorageFailureMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try { await next(context); }
        catch (StorageException error) when (context.Request.Path.StartsWithSegments("/api"))
        {
            logger.LogWarning(error, "Recoverable storage failure Code={Code} Trace={Trace} Path={Path}", error.Code, context.TraceIdentifier, context.Request.Path);
            if (context.Response.HasStarted) { context.Abort(); return; }
            context.Response.Clear();
            context.Response.Headers.CacheControl = "no-store";
            int status = error.Code == "STORAGE-MISSING-001" ? StatusCodes.Status404NotFound
                : error.Code.Contains("INTEGRITY", StringComparison.OrdinalIgnoreCase) ? StatusCodes.Status409Conflict
                : StatusCodes.Status503ServiceUnavailable;
            string message = status == 404 ? "This file is unavailable. Retry the demo load for seeded photos, or restore the original file from storage."
                : "Storage is temporarily unavailable or the file failed its integrity check. Retry after checking storage.";
            await Results.Problem(statusCode: status, title: "File unavailable", detail: message,
                extensions: new Dictionary<string, object?> { ["code"] = error.Code, ["traceId"] = context.TraceIdentifier }).ExecuteAsync(context);
        }
    }
}
