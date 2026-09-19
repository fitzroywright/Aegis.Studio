namespace Aegis.Studio.WorkDay;

public sealed class WorkDaySessionContext
{
    public string? UserName { get; set; }
    public string? DisplayName { get; set; }
    public bool IsAuthenticated { get; set; }
    public Uri? Server { get; set; }
}

public sealed class WorkDayDeviceIdentity
{
    private const string Key="Aegis.Studio.WorkDay.DeviceId";
    private string? cached;
    public async Task<string> GetAsync()
    {
        if(!string.IsNullOrWhiteSpace(cached)) return cached;
        try { cached=await SecureStorage.Default.GetAsync(Key); } catch { cached=null; }
        if(string.IsNullOrWhiteSpace(cached))
        {
            cached=Guid.NewGuid().ToString("D");
            try { await SecureStorage.Default.SetAsync(Key,cached); } catch { }
        }
        return cached;
    }
}
