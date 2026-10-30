using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Common.Messaging;

namespace Aegis.Studio.Web.Email;

// One Studio process owns this cache. The encryption key belongs in Common.Secrets,
// outside the cache directory. Access/refresh tokens never leave the server.
public sealed class StudioDelegatedGraphSession(IConfiguration configuration, IChannelSecretResolver secrets, IHttpClientFactory clients)
{
    public const string Prefix = "Messaging:MicrosoftGraph:Email:Delegated:";
    public const string CallbackPath = "/oauth/outbound-mail/callback";
    private const string Scopes = "offline_access https://graph.microsoft.com/User.Read https://graph.microsoft.com/Mail.Send.Shared https://graph.microsoft.com/Mail.Read";
    private readonly SemaphoreSlim gate = new(1, 1);
    private long generation;
    public long Generation => Interlocked.Read(ref generation);
    public bool Enabled => !Aegis.Studio.Web.Operations.DemoIntegrationIsolation.Enabled(configuration) && configuration.GetValue<bool>(Prefix + "Enabled");
    private string CachePath => Path.Combine(configuration[Prefix + "CacheDirectory"] ?? "/var/lib/aegis/studio/outbound-oauth", "session.enc");
    private string Hub => StudioGraphMailbox.Resolve(configuration);
    private string Tenant
    {
        get
        {
            string tenant = configuration["Messaging:MicrosoftGraph:TenantId"] ?? "";
            if (!Guid.TryParse(tenant, out _)) throw new InvalidOperationException("A tenant GUID is required for delegated outbound mail.");
            return tenant;
        }
    }
    private string ClientId => configuration["Messaging:MicrosoftGraph:ClientId"] ?? throw new InvalidOperationException("Graph ClientId is required.");
    public string RedirectUri
    {
        get
        {
            string value = configuration[Prefix + "RedirectUri"] ?? "";
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                uri.AbsolutePath != CallbackPath || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
                throw new InvalidOperationException("Configure an HTTPS delegated RedirectUri ending in " + CallbackPath + ".");
            return value;
        }
    }
    public async Task<string> AuthorizationUrlAsync(string state, string verifier, CancellationToken token)
    {
        if (!Enabled) throw new InvalidOperationException("Delegated outbound mail is not enabled.");
        _ = await EncryptionKeyAsync(token); // Fail before redirecting if persistence cannot be protected.
        return $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/authorize?" + Query(new()
        {
            ["client_id"] = ClientId, ["response_type"] = "code", ["response_mode"] = "query",
            ["redirect_uri"] = RedirectUri, ["scope"] = Scopes, ["state"] = state,
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256", ["login_hint"] = Hub, ["prompt"] = "select_account"
        });
    }
    public async Task CompleteAsync(string code, string verifier, CancellationToken token, long? expectedGeneration = null)
    {
        await gate.WaitAsync(token);
        try
        {
            if (!Enabled || (expectedGeneration.HasValue && expectedGeneration != Generation)) throw new StudioMailReconnectRequiredException();
            var session = await ExchangeAsync(new() { ["grant_type"] = "authorization_code", ["code"] = code,
                ["redirect_uri"] = RedirectUri, ["code_verifier"] = verifier }, null, token);
            string account = await VerifyHubAsync(session.AccessToken, token);
            await SaveAsync(session with { AccountId = account }, token);
        }
        finally { gate.Release(); }
    }
    public async Task<string> AccessTokenAsync(CancellationToken token)
    {
        if (!Enabled) throw new StudioMailReconnectRequiredException();
        await gate.WaitAsync(token);
        try
        {
            var session = await LoadAsync(token) ?? throw new StudioMailReconnectRequiredException();
            if (session.Tenant != Tenant || session.ClientId != ClientId || !string.Equals(session.Hub, Hub, StringComparison.OrdinalIgnoreCase))
                throw new StudioMailReconnectRequiredException();
            if (session.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return session.AccessToken;
            var renewed = await ExchangeAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = session.RefreshToken }, session, token);
            // Ensure refresh still belongs to the account explicitly authorized by the administrator.
            if (await VerifyHubAsync(renewed.AccessToken, token) != session.AccountId) throw new StudioMailReconnectRequiredException();
            await SaveAsync(renewed, token);
            return renewed.AccessToken;
        }
        catch (StudioMailReconnectRequiredException) { DeleteCache(); throw; }
        finally { gate.Release(); }
    }
    public async Task<OutboundMailboxConnectionStatus> StatusAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var session = await LoadAsync(token);
            return new(Enabled, session is not null && session.Hub.Equals(Hub, StringComparison.OrdinalIgnoreCase)
                && session.ClientId == ClientId && session.Tenant == Tenant, Hub, session?.AuthorizedAt);
        }
        finally { gate.Release(); }
    }
    public async Task DisconnectAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { Interlocked.Increment(ref generation); DeleteCache(); }
        finally { gate.Release(); }
    }
    private void DeleteCache()
    {
        // Disconnect is idempotent, including before the first successful connection.
        // Ignore only absence; permission and other I/O failures must remain visible.
        try { File.Delete(CachePath); }
        catch (DirectoryNotFoundException) { }
        catch (FileNotFoundException) { }
    }

    private async Task<Session> ExchangeAsync(Dictionary<string,string> fields, Session? previous, CancellationToken token)
    {
        fields["client_id"] = ClientId;
        fields["client_secret"] = await secrets.GetSecretAsync(configuration["Messaging:MicrosoftGraph:ClientSecretName"] ?? "messaging/msgraph/client-secret", token)
            ?? throw new InvalidOperationException("Graph client secret is unavailable.");
        fields["scope"] = Scopes;
        using var http = clients.CreateClient("StudioOutboundOAuth");
        using var response = await http.PostAsync($"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token", new FormUrlEncodedContent(fields), token);
        if (!response.IsSuccessStatusCode)
        {
            // Never put token endpoint bodies (or token-bearing URLs) into application logs.
            if ((int)response.StatusCode is 400 or 401) throw new StudioMailReconnectRequiredException();
            throw new HttpRequestException("Outbound OAuth token service failed.", null, response.StatusCode);
        }
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var root = json.RootElement;
        var granted = root.GetProperty("scope").GetString()?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        foreach (string required in new[] { "User.Read", "Mail.Send.Shared", "Mail.Read" })
            if (!granted.Any(s => s.Equals(required, StringComparison.OrdinalIgnoreCase) || s.EndsWith("/" + required, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Required delegated Graph permissions were not granted.");
        string refresh = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : previous?.RefreshToken ?? "";
        if (string.IsNullOrWhiteSpace(refresh)) throw new StudioMailReconnectRequiredException();
        return new(root.GetProperty("access_token").GetString()!, refresh,
            DateTimeOffset.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32()), Hub, Tenant, ClientId,
            previous?.AccountId ?? "", previous?.AuthorizedAt ?? DateTimeOffset.UtcNow);
    }
    private async Task<string> VerifyHubAsync(string bearer, CancellationToken token)
    {
        using var http = clients.CreateClient("StudioOutboundOAuth");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me?$select=id,mail,userPrincipalName");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var response = await http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw new StudioMailReconnectRequiredException();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var root = json.RootElement;
        string? mail = root.TryGetProperty("mail", out var m) ? m.GetString() : null;
        string? upn = root.TryGetProperty("userPrincipalName", out var u) ? u.GetString() : null;
        if (!string.Equals(Hub, mail, StringComparison.OrdinalIgnoreCase) && !string.Equals(Hub, upn, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The signed-in Microsoft account is not the configured hub mailbox. Connect again using the hub account.");
        return root.GetProperty("id").GetString() ?? throw new StudioMailReconnectRequiredException();
    }
    private async Task<byte[]> EncryptionKeyAsync(CancellationToken token)
    {
        string name = configuration[Prefix + "EncryptionKeySecretName"] ?? "messaging/msgraph/outbound-cache-key";
        string value = await secrets.GetSecretAsync(name, token) ?? "";
        byte[] key;
        try { key = Convert.FromBase64String(value); }
        catch (FormatException) { throw new InvalidOperationException("Outbound cache encryption key must be 32 random bytes encoded as base64."); }
        if (key.Length != 32) throw new InvalidOperationException("Outbound cache encryption key must be 32 random bytes encoded as base64.");
        return key;
    }
    private async Task SaveAsync(Session session, CancellationToken token)
    {
        byte[] key = await EncryptionKeyAsync(token);
        byte[] encrypted = Encrypt(JsonSerializer.SerializeToUtf8Bytes(session), key);
        CryptographicOperations.ZeroMemory(key);
        string directory = Path.GetDirectoryName(CachePath)!;
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string temporary = CachePath + "." + Guid.NewGuid().ToString("N");
        try
        {
            var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var file = new FileStream(temporary, fileOptions))
            { await file.WriteAsync(encrypted, token); file.Flush(true); }
            File.Move(temporary, CachePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private async Task<Session?> LoadAsync(CancellationToken token)
    {
        if (!File.Exists(CachePath)) return null;
        byte[] key = await EncryptionKeyAsync(token);
        try { return JsonSerializer.Deserialize<Session>(Decrypt(await File.ReadAllBytesAsync(CachePath, token), key)); }
        catch (Exception ex) when (ex is CryptographicException or JsonException) { throw new StudioMailReconnectRequiredException(); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    public static byte[] Encrypt(byte[] plaintext, byte[] key)
    {
        byte[] value = new byte[28 + plaintext.Length];
        RandomNumberGenerator.Fill(value.AsSpan(0, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(value.AsSpan(0, 12), plaintext, value.AsSpan(28), value.AsSpan(12, 16), Encoding.UTF8.GetBytes("Aegis.Studio.OutboundOAuth.v1"));
        return value;
    }
    public static byte[] Decrypt(byte[] value, byte[] key)
    {
        if (value.Length < 28) throw new CryptographicException("Invalid outbound token cache.");
        byte[] plain = new byte[value.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(value.AsSpan(0, 12), value.AsSpan(28), value.AsSpan(12, 16), plain, Encoding.UTF8.GetBytes("Aegis.Studio.OutboundOAuth.v1"));
        return plain;
    }
    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Query(Dictionary<string,string> values) => string.Join("&", values.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
    private sealed record Session(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, string Hub, string Tenant, string ClientId, string AccountId, DateTimeOffset AuthorizedAt);
}

public sealed class StudioMailReconnectRequiredException() : Exception("Outbound hub authorization is unavailable or expired. An administrator must reconnect the outbound mailbox.") { }

public sealed record OutboundMailboxConnectionStatus(bool Enabled, bool Connected, string Mailbox, DateTimeOffset? AuthorizedAt);
