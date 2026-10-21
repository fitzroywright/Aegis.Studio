using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;

namespace Aegis.Studio.Web.Email;

public static class StudioOutboundMailboxEndpoints
{
    private sealed record Pending(string Verifier, string Browser, DateTimeOffset ExpiresAt);

    public static void MapStudioOutboundMailbox(this WebApplication app)
    {
        const string page = "/admin/outbound-mail";
        const string cookie = "__Host-StudioOutboundOAuth";
        // Bounded, expiring, single-use authorization attempts. A restart requires Connect again.
        var pending = new ConcurrentDictionary<string, Pending>();
        app.MapGet(page, async (HttpContext context, IAntiforgery antiforgery, StudioDelegatedGraphSession session) =>
        {
            NoStore(context);
            string status;
            try { status = System.Text.Json.JsonSerializer.Serialize(await session.StatusAsync(context.RequestAborted)); }
            catch (Exception ex) when (ex is InvalidOperationException or StudioMailReconnectRequiredException or CryptographicException)
            { status = ex.Message; }
            var csrf = antiforgery.GetAndStoreTokens(context);
            string hidden = $"<input type='hidden' name='{HtmlEncoder.Default.Encode(csrf.FormFieldName)}' value='{HtmlEncoder.Default.Encode(csrf.RequestToken ?? "")}' />";
            return Results.Content("<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width'><title>Outbound mailbox</title>" +
                "<style>body{font:16px system-ui;margin:0;background:#f6f3ed;color:#223}header{background:#153451;color:white;padding:20px;border-bottom:3px solid #dc852b}main{max-width:760px;margin:24px auto;padding:20px}button{background:#765338;color:white;padding:12px;border:0;border-radius:5px;cursor:pointer}form{display:inline-block;margin:12px 12px 12px 0}pre{white-space:pre-wrap}a{color:#765338}</style></head><body>" +
                "<header>Brian’s · Outbound mailbox</header><main><h1>Connect outbound mailbox</h1><p>Sign in to Microsoft using the configured hub account. Studio never stores its password.</p><pre>" + HtmlEncoder.Default.Encode(status) + "</pre>" +
                $"<form method='post' action='{page}/connect'>{hidden}<button>Connect outbound mailbox</button></form>" +
                $"<form method='post' action='{page}/disconnect'>{hidden}<button>Disconnect</button></form>" +
                "<p>Disconnect removes Studio’s local authorization. Previously accepted deliveries are not resent.</p><a href='/'>Return to Today</a></main></body></html>", "text/html; charset=utf-8");
        }).RequireAuthorization(policy => policy.RequireRole("Administrator"));

        app.MapPost(page + "/connect", async (HttpContext context, IAntiforgery antiforgery, StudioDelegatedGraphSession session) =>
        {
            NoStore(context);
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return Results.BadRequest("Refresh the mailbox page and try again."); }
            foreach (var p in pending.Where(p => p.Value.ExpiresAt <= DateTimeOffset.UtcNow)) pending.TryRemove(p.Key, out _);
            if (pending.Count >= 20) return Results.BadRequest("Too many pending connections. Wait ten minutes and try again.");
            string state = StudioDelegatedGraphSession.Base64Url(RandomNumberGenerator.GetBytes(32));
            string browser = StudioDelegatedGraphSession.Base64Url(RandomNumberGenerator.GetBytes(32));
            string verifier = StudioDelegatedGraphSession.Base64Url(RandomNumberGenerator.GetBytes(48));
            string url;
            try { url = await session.AuthorizationUrlAsync(state, verifier, context.RequestAborted); }
            catch (InvalidOperationException ex) { return Results.BadRequest(ex.Message); }
            pending[state] = new(verifier, browser, DateTimeOffset.UtcNow.AddMinutes(10));
            context.Response.Cookies.Append(cookie, browser, new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = "/", MaxAge = TimeSpan.FromMinutes(10), IsEssential = true });
            return Results.Redirect(url);
        }).RequireAuthorization(policy => policy.RequireRole("Administrator"));

        // The Studio sign-in cookie uses SameSite=Strict. The callback is instead bound to the
        // administrator-initiated attempt by random state + a separate Secure/Lax correlation cookie.
        app.MapGet(StudioDelegatedGraphSession.CallbackPath, async (HttpContext context, StudioDelegatedGraphSession session) =>
        {
            NoStore(context);
            string state = context.Request.Query["state"].ToString();
            if (!session.Enabled || !pending.TryRemove(state, out var attempt) || attempt.ExpiresAt <= DateTimeOffset.UtcNow ||
                !context.Request.Cookies.TryGetValue(cookie, out var browser) ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(browser), Encoding.UTF8.GetBytes(attempt.Browser)))
                return Results.BadRequest("Mailbox connection expired or invalid. Start again from the administrator page.");
            context.Response.Cookies.Delete(cookie, new CookieOptions { Secure = true, Path = "/" });
            string code = context.Request.Query["code"].ToString();
            if (context.Request.Query.ContainsKey("error") || string.IsNullOrWhiteSpace(code))
                return Results.BadRequest("Microsoft sign-in was cancelled or denied. Start again from the administrator page.");
            try { await session.CompleteAsync(code, attempt.Verifier, context.RequestAborted); }
            catch (Exception ex) when (ex is InvalidOperationException or StudioMailReconnectRequiredException)
            { return Results.BadRequest(ex.Message); }
            return Results.Redirect(page);
        }).AllowAnonymous();

        app.MapPost(page + "/disconnect", async (HttpContext context, IAntiforgery antiforgery, StudioDelegatedGraphSession session) =>
        {
            NoStore(context);
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return Results.BadRequest("Refresh the mailbox page and try again."); }
            pending.Clear();
            await session.DisconnectAsync(context.RequestAborted);
            return Results.Redirect(page);
        }).RequireAuthorization(policy => policy.RequireRole("Administrator"));
    }

    private static void NoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Frame-Options"] = "DENY";
    }
}
