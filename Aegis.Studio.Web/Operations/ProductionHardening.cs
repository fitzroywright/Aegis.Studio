using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Aegis.Studio.Infrastructure.Email;
using Aegis.Studio.Web.Bookings;
using Aegis.Studio.Web.Persistence;
using Microsoft.AspNetCore.Authorization;
using Npgsql;

namespace Aegis.Studio.Web.Operations;

public static class StudioProductionHardening
{
    public static void MapStudioProductionHardening(this WebApplication app)
    {
        StudioDatabase database = app.Services.GetRequiredService<StudioDatabase>();
        ProductionAuditStore audit = new(database);
        ProductionRateLimitStore rateLimits = new(database);
        audit.EnsureSchema();
        rateLimits.EnsureSchema();

        IConfiguration configuration = app.Services.GetRequiredService<IConfiguration>();
        IWebHostEnvironment environment = app.Services.GetRequiredService<IWebHostEnvironment>();

        app.Use(async (context, next) =>
        {
            string correlationId = context.Request.Headers.TryGetValue("X-Correlation-ID", out Microsoft.Extensions.Primitives.StringValues supplied)
                && supplied.Count == 1
                && IsSafeCorrelationId(supplied[0])
                    ? supplied[0]!
                    : Guid.NewGuid().ToString("N");
            context.TraceIdentifier = correlationId;
            context.Response.Headers["X-Correlation-ID"] = correlationId;

            ApplySecurityHeaders(context, environment);

            if (ShouldRejectPlainHttp(context, configuration, environment))
            {
                string target = "https://" + context.Request.Host + context.Request.PathBase + context.Request.Path + context.Request.QueryString;
                context.Response.Redirect(target, permanent: true);
                return;
            }

            if (RequestTooLarge(context, configuration, out long maximumBytes))
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                await context.Response.WriteAsJsonAsync(new { error = $"Request exceeds the configured {maximumBytes / 1024 / 1024} MB limit.", correlationId });
                return;
            }

            RateLimitPolicy? policy = ProductionRequestPolicy.GetRateLimitPolicy(context.Request);
            if (policy is not null)
            {
                string clientKey = HashClientKey(context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
                RateLimitDecision decision = rateLimits.Consume(policy.Scope, clientKey, policy.Window, policy.PermitLimit);
                context.Response.Headers["X-RateLimit-Limit"] = policy.PermitLimit.ToString(System.Globalization.CultureInfo.InvariantCulture);
                context.Response.Headers["X-RateLimit-Remaining"] = decision.Remaining.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!decision.Allowed)
                {
                    context.Response.Headers["Retry-After"] = Math.Max(1, (int)Math.Ceiling((decision.ResetsAtUtc - DateTimeOffset.UtcNow).TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    await context.Response.WriteAsJsonAsync(new { error = "Too many requests. Try again shortly.", correlationId });
                    return;
                }
            }

            if (ProductionRequestPolicy.IsPublicGalleryOriginal(context.Request)
                && !configuration.GetValue<bool>("Galleries:AllowPublicOriginalDownloads"))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            Exception? failure = null;
            try
            {
                await next();
            }
            catch (Exception exception)
            {
                failure = exception;
                app.Logger.LogError(exception, "Unhandled Studio request failure {Method} {Path} correlation {CorrelationId}", context.Request.Method, context.Request.Path, correlationId);
                if (!context.Response.HasStarted)
                {
                    context.Response.Clear();
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    if (context.Request.Path.StartsWithSegments("/api"))
                    {
                        await context.Response.WriteAsJsonAsync(new { error = "The request could not be completed.", correlationId });
                    }
                    else
                    {
                        context.Response.ContentType = "text/plain; charset=utf-8";
                        await context.Response.WriteAsync($"The request could not be completed. Reference: {correlationId}");
                    }
                }
            }
            finally
            {
                stopwatch.Stop();
                if (ProductionRequestPolicy.ShouldAudit(context.Request))
                {
                    try
                    {
                        audit.Record(context, stopwatch.ElapsedMilliseconds, failure?.GetType().Name);
                    }
                    catch (Exception auditException)
                    {
                        app.Logger.LogError(auditException, "Failed to persist audit record for correlation {CorrelationId}", correlationId);
                    }
                }
            }
        });

        RouteGroupBuilder admin = app.MapGroup("/api/admin/operations").RequireAuthorization(new AuthorizeAttribute { Roles = "Administrator" });
        admin.MapGet("/audit", (int? count) => Results.Ok(audit.GetRecent(Math.Clamp(count ?? 200, 1, 2000))));
        admin.MapGet("/readiness", () =>
        {
            ProductionReadinessReport report = ProductionReadiness.Check(app.Services, environment, configuration);
            return report.IsReady ? Results.Ok(report) : Results.Json(report, statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
        app.MapGet("/health/ready", () =>
        {
            ProductionReadinessReport report = ProductionReadiness.Check(app.Services, environment, configuration);
            return report.IsReady
                ? Results.Ok(new { status = "ready" })
                : Results.Json(new { status = "not-ready" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }).AllowAnonymous();
    }

    private static bool IsSafeCorrelationId(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= 80 && value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.');

    private static void ApplySecurityHeaders(HttpContext context, IWebHostEnvironment environment)
    {
        context.Response.OnStarting(() =>
        {
            IHeaderDictionary headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            headers["Content-Security-Policy"] = "frame-ancestors 'none'; base-uri 'self'; object-src 'none'";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            if (!environment.IsDevelopment() && context.Request.IsHttps)
            {
                headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
            }
            if (context.Request.Path.StartsWithSegments("/api") && !context.Request.Path.StartsWithSegments("/api/public"))
            {
                headers["Cache-Control"] = "no-store";
                headers["Pragma"] = "no-cache";
            }
            if (context.Request.Path.StartsWithSegments("/g") || context.Request.Path.StartsWithSegments("/public/galleries"))
            {
                headers["X-Robots-Tag"] = "noindex, nofollow, noarchive";
            }
            return Task.CompletedTask;
        });
    }

    private static bool ShouldRejectPlainHttp(HttpContext context, IConfiguration configuration, IWebHostEnvironment environment)
    {
        if (environment.IsDevelopment()) return false;
        if (!configuration.GetValue("Security:RequireHttps", true)) return false;
        if (context.Request.IsHttps) return false;
        bool trustForwardedProto = configuration.GetValue("Security:TrustForwardedProto", false);
        return !(trustForwardedProto && string.Equals(context.Request.Headers["X-Forwarded-Proto"].ToString(), "https", StringComparison.OrdinalIgnoreCase));
    }

    private static bool RequestTooLarge(HttpContext context, IConfiguration configuration, out long maximumBytes)
    {
        bool multipart = context.Request.HasFormContentType;
        int maximumMegabytes = multipart
            ? Math.Clamp(configuration.GetValue<int?>("Security:MaxMultipartMegabytes") ?? 300, 1, 1024)
            : Math.Clamp(configuration.GetValue<int?>("Security:MaxRequestMegabytes") ?? 8, 1, 100);
        maximumBytes = maximumMegabytes * 1024L * 1024L;
        return context.Request.ContentLength.HasValue && context.Request.ContentLength.Value > maximumBytes;
    }

    private static string HashClientKey(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public static class ProductionRequestPolicy
{
    public static RateLimitPolicy? GetRateLimitPolicy(HttpRequest request)
    {
        string path = request.Path.Value ?? string.Empty;
        if (path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase)) return new RateLimitPolicy("auth-login", 10, TimeSpan.FromMinutes(1));
        if (request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) && path.Equals("/api/bookings", StringComparison.OrdinalIgnoreCase)) return new RateLimitPolicy("public-booking", 20, TimeSpan.FromMinutes(1));
        if (request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) && path.Contains("/payment-confirmations", StringComparison.OrdinalIgnoreCase)) return new RateLimitPolicy("payment-confirmation", 30, TimeSpan.FromMinutes(1));
        if (path.StartsWith("/public/galleries/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/g/", StringComparison.OrdinalIgnoreCase)) return new RateLimitPolicy("public-gallery", 300, TimeSpan.FromMinutes(1));
        return null;
    }

    public static bool IsPublicGalleryOriginal(HttpRequest request)
        => request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase)
        && request.Path.StartsWithSegments("/public/galleries")
        && request.Path.Value?.EndsWith("/original", StringComparison.OrdinalIgnoreCase) == true;

    public static bool ShouldAudit(HttpRequest request)
        => request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)
        || request.Method.Equals("PUT", StringComparison.OrdinalIgnoreCase)
        || request.Method.Equals("PATCH", StringComparison.OrdinalIgnoreCase)
        || request.Method.Equals("DELETE", StringComparison.OrdinalIgnoreCase);
}

public sealed record RateLimitPolicy(string Scope, int PermitLimit, TimeSpan Window);
public sealed record RateLimitDecision(bool Allowed, int Remaining, DateTimeOffset ResetsAtUtc);

public sealed class ProductionRateLimitStore
{
    private readonly StudioDatabase database;

    public ProductionRateLimitStore(StudioDatabase database) => this.database = database;

    public void EnsureSchema()
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            create table if not exists studio_api_rate_limits
            (
                scope text not null,
                client_key text not null,
                window_start_utc timestamptz not null,
                request_count integer not null,
                updated_at_utc timestamptz not null,
                primary key(scope,client_key,window_start_utc)
            );
            create index if not exists ix_studio_api_rate_limits_updated on studio_api_rate_limits(updated_at_utc);
            """;
        command.ExecuteNonQuery();
    }

    public RateLimitDecision Consume(string scope, string clientKey, TimeSpan window, int permitLimit)
    {
        long seconds = Math.Max(1L, (long)window.TotalSeconds);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        long unix = now.ToUnixTimeSeconds();
        long bucket = unix - unix % seconds;
        DateTimeOffset windowStart = DateTimeOffset.FromUnixTimeSeconds(bucket);
        DateTimeOffset resetsAt = windowStart.AddSeconds(seconds);

        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlTransaction transaction = connection.BeginTransaction();
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into studio_api_rate_limits(scope,client_key,window_start_utc,request_count,updated_at_utc)
            values($1,$2,$3,1,now())
            on conflict(scope,client_key,window_start_utc)
            do update set request_count=studio_api_rate_limits.request_count+1,updated_at_utc=now()
            returning request_count
            """;
        command.Parameters.AddWithValue(scope);
        command.Parameters.AddWithValue(clientKey);
        command.Parameters.AddWithValue(windowStart);
        int count = Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        using NpgsqlCommand cleanup = connection.CreateCommand();
        cleanup.Transaction = transaction;
        cleanup.CommandText = "delete from studio_api_rate_limits where updated_at_utc < now() - interval '1 day'";
        cleanup.ExecuteNonQuery();
        transaction.Commit();
        return new RateLimitDecision(count <= permitLimit, Math.Max(0, permitLimit - count), resetsAt);
    }
}

public sealed class ProductionAuditStore
{
    private readonly StudioDatabase database;

    public ProductionAuditStore(StudioDatabase database) => this.database = database;

    public void EnsureSchema()
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            create table if not exists studio_operational_audit
            (
                id uuid primary key,
                occurred_at_utc timestamptz not null,
                correlation_id text not null,
                user_name text not null,
                method text not null,
                path text not null,
                status_code integer not null,
                duration_ms bigint not null,
                client_key text null,
                failure_type text null
            );
            create index if not exists ix_studio_operational_audit_occurred on studio_operational_audit(occurred_at_utc desc);
            create index if not exists ix_studio_operational_audit_user on studio_operational_audit(user_name,occurred_at_utc desc);
            """;
        command.ExecuteNonQuery();
    }

    public void Record(HttpContext context, long durationMilliseconds, string? failureType)
    {
        string userName = context.User.FindFirstValue("login_name")
            ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? context.User.Identity?.Name
            ?? "ANONYMOUS";
        string client = context.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
        string clientKey = string.IsNullOrWhiteSpace(client) ? string.Empty : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(client))).ToLowerInvariant();
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "insert into studio_operational_audit(id,occurred_at_utc,correlation_id,user_name,method,path,status_code,duration_ms,client_key,failure_type) values($1,now(),$2,$3,$4,$5,$6,$7,$8,$9)";
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue(context.TraceIdentifier);
        command.Parameters.AddWithValue(userName);
        command.Parameters.AddWithValue(context.Request.Method);
        command.Parameters.AddWithValue(context.Request.Path.Value ?? string.Empty);
        command.Parameters.AddWithValue(context.Response.StatusCode);
        command.Parameters.AddWithValue(durationMilliseconds);
        command.Parameters.AddWithValue(clientKey);
        command.Parameters.AddWithValue((object?)failureType ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<ProductionAuditEntry> GetRecent(int count)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id,occurred_at_utc,correlation_id,user_name,method,path,status_code,duration_ms,failure_type from studio_operational_audit order by occurred_at_utc desc limit $1";
        command.Parameters.AddWithValue(count);
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<ProductionAuditEntry> values = [];
        while (reader.Read())
        {
            values.Add(new ProductionAuditEntry(reader.GetGuid(0),reader.GetFieldValue<DateTimeOffset>(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.GetInt32(6),reader.GetInt64(7),reader.IsDBNull(8)?null:reader.GetString(8)));
        }
        return values;
    }
}

public sealed record ProductionAuditEntry(Guid Id,DateTimeOffset OccurredAtUtc,string CorrelationId,string UserName,string Method,string Path,int StatusCode,long DurationMilliseconds,string? FailureType);

public static class ProductionReadiness
{
    public static ProductionReadinessReport Check(IServiceProvider services, IWebHostEnvironment environment, IConfiguration configuration)
    {
        List<ProductionReadinessCheck> checks = [];
        StudioDatabase database = services.GetRequiredService<StudioDatabase>();
        try
        {
            using NpgsqlConnection connection = database.OpenConnection();
            using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText = "select 1";
            command.ExecuteScalar();
            checks.Add(new ProductionReadinessCheck("PostgreSQL", true, "Database connection is available."));
        }
        catch (Exception exception)
        {
            checks.Add(new ProductionReadinessCheck("PostgreSQL", false, exception.Message));
        }

        BookingApiSecurityStatus booking = services.GetRequiredService<BookingApiAuthenticator>().GetStatus();
        checks.Add(new ProductionReadinessCheck("Public booking authentication", booking.IsReady, booking.IsReady ? $"{booking.ConfiguredClients} HMAC client(s) configured." : "No HMAC booking client secret is configured."));

        MicrosoftGraphEmailClient graphClient = services.GetRequiredService<MicrosoftGraphEmailClient>();
        MicrosoftGraphEmailOptions graphOptions = services.GetRequiredService<MicrosoftGraphEmailOptions>();
        bool graphReady = !graphOptions.Enabled || graphClient.IsConfigured;
        checks.Add(new ProductionReadinessCheck("Microsoft Graph email", graphReady, graphReady ? (graphOptions.Enabled ? "Enabled and configured." : "Disabled by configuration.") : "Enabled but missing required credentials."));

        checks.Add(CheckStorage("Attachments storage", configuration["Storage:AttachmentsPath"], Path.Combine(environment.ContentRootPath,"Storage","attachments")));
        checks.Add(CheckStorage("Gallery storage", configuration["Storage:GalleriesPath"], Path.Combine(environment.ContentRootPath,"Storage","galleries")));
        checks.Add(CheckStorage("Media archive", configuration["Storage:MediaArchivePath"], Path.Combine(environment.ContentRootPath,"Storage","media")));

        bool production = !environment.IsDevelopment();
        bool requireHttps = configuration.GetValue("Security:RequireHttps", true);
        checks.Add(new ProductionReadinessCheck("HTTPS policy", !production || requireHttps, !production ? "Development environment." : requireHttps ? "HTTPS is required." : "HTTPS enforcement is disabled in a non-development environment."));

        return new ProductionReadinessReport(checks.All(x => x.Passed), environment.EnvironmentName, DateTimeOffset.UtcNow, checks);
    }

    private static ProductionReadinessCheck CheckStorage(string name, string? configuredPath, string fallback)
    {
        string path = string.IsNullOrWhiteSpace(configuredPath) ? fallback : Path.GetFullPath(configuredPath);
        try
        {
            Directory.CreateDirectory(path);
            string probe = Path.Combine(path, ".studio-write-probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            string detail = string.IsNullOrWhiteSpace(configuredPath) ? $"Writable using local fallback {path}. Configure an explicit production path for multi-server deployment." : $"Writable at {path}.";
            return new ProductionReadinessCheck(name, true, detail);
        }
        catch (Exception exception)
        {
            return new ProductionReadinessCheck(name, false, exception.Message);
        }
    }
}

public sealed record ProductionReadinessReport(bool IsReady,string Environment,DateTimeOffset CheckedAtUtc,IReadOnlyList<ProductionReadinessCheck> Checks);
public sealed record ProductionReadinessCheck(string Name,bool Passed,string Detail);
