using System.Diagnostics;

namespace Aegis.Studio.Web.Operations;

public sealed class TodayStartupTimingMiddleware(RequestDelegate next, ILogger<TodayStartupTimingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        string run = context.Request.Headers["X-Studio-Startup"].ToString();
        if (!context.Request.Path.StartsWithSegments("/api/today") && string.IsNullOrEmpty(run))
        {
            await next(context);
            return;
        }
        if (run.Length > 80) run = run[..80];
        Stopwatch timer = Stopwatch.StartNew();
        context.Response.Headers["X-Studio-Trace"] = context.TraceIdentifier;
        logger.LogInformation("Today startup begin Run={Run} Trace={Trace} Path={Path}", run, context.TraceIdentifier, context.Request.Path);
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.Append("Server-Timing", $"today;dur={timer.Elapsed.TotalMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}");
            return Task.CompletedTask;
        });
        bool failed = false;
        try { await next(context); }
        catch (Exception error)
        {
            failed = true;
            logger.LogError(error, "Today startup failed Run={Run} Trace={Trace} Path={Path} ElapsedMs={ElapsedMs}", run, context.TraceIdentifier, context.Request.Path, timer.Elapsed.TotalMilliseconds);
            throw;
        }
        finally
        {
            logger.LogInformation("Today startup request Run={Run} Trace={Trace} Path={Path} Status={Status} Failed={Failed} Cancelled={Cancelled} ElapsedMs={ElapsedMs}", run, context.TraceIdentifier, context.Request.Path, context.Response.StatusCode, failed, context.RequestAborted.IsCancellationRequested, timer.Elapsed.TotalMilliseconds);
        }
    }
}
