namespace Aegis.Studio.WorkDay;

public sealed class WorkDayCredentialStore
{
    private const string TokenKey="Aegis.Studio.WorkDay.DeviceToken";
    private const string ExpiryKey="Aegis.Studio.WorkDay.DeviceTokenExpiry";
    public async Task SaveAsync(string token,DateTimeOffset expiresAtUtc)
    {
        await SecureStorage.Default.SetAsync(TokenKey,token);
        await SecureStorage.Default.SetAsync(ExpiryKey,expiresAtUtc.ToString("O"));
    }
    public async Task<(string Token,DateTimeOffset ExpiresAtUtc)?> GetAsync()
    {
        try
        {
            string? token=await SecureStorage.Default.GetAsync(TokenKey);
            string? expiry=await SecureStorage.Default.GetAsync(ExpiryKey);
            if(string.IsNullOrWhiteSpace(token)||!DateTimeOffset.TryParse(expiry,out var at)||at<=DateTimeOffset.UtcNow)return null;
            return(token,at);
        }
        catch{return null;}
    }
    public void Clear(){try{SecureStorage.Default.Remove(TokenKey);SecureStorage.Default.Remove(ExpiryKey);}catch{}}
}
