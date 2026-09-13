using System.Net;
using System.Net.Http.Json;

namespace Aegis.Studio.Photographer;

public sealed class StudioApiClient
{
    private readonly CookieContainer cookies = new();
    private readonly HttpClient http;

    public StudioApiClient()
    {
        http = new HttpClient(new HttpClientHandler { CookieContainer = cookies })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public void SetServer(string serverUrl)
    {
        if (!Uri.TryCreate(serverUrl?.Trim().TrimEnd('/') + "/", UriKind.Absolute, out Uri? uri))
            throw new InvalidOperationException("Enter a valid Studio server URL.");
        http.BaseAddress = uri;
    }

    public async Task LoginAsync(string userName, string password, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response = await http.PostAsJsonAsync("api/auth/login", new { userName, password }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new InvalidOperationException("Sign-in failed.");
        response.EnsureSuccessStatusCode();
    }

    public async Task<PhotographerProfile?> GetMeAsync(CancellationToken cancellationToken = default)
        => await http.GetFromJsonAsync<PhotographerProfile>("api/mobile/photographer/me", cancellationToken);

    public async Task<IReadOnlyList<ScheduleEntry>> GetScheduleAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        string url = $"api/mobile/photographer/schedule?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";
        return await http.GetFromJsonAsync<List<ScheduleEntry>>(url, cancellationToken) ?? [];
    }

    public async Task AddAvailabilityAsync(DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc, string availabilityType, string? note, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response = await http.PostAsJsonAsync("api/mobile/photographer/availability", new { startsAtUtc, endsAtUtc, availabilityType, note }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}

public sealed record PhotographerProfile(string UserName, string DisplayName, string EmploymentType, bool IsActive, IReadOnlyList<string> Skills, string? DefaultEquipmentNotes, Guid ScheduleResourceId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record ScheduleEntry(Guid Id, Guid? JobId, Guid? BookingId, string Title, DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc, string? Location, string Status, string? Notes, DateTimeOffset CreatedAtUtc, IReadOnlyList<Guid> ResourceIds);
