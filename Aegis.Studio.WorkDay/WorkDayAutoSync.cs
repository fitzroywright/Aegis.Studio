using Microsoft.Extensions.Logging;

namespace Aegis.Studio.WorkDay;

// Mobile platforms suspend apps: sync on reconnect while active, and on resume.
// Periodic retries also handle reachable Wi-Fi whose Studio server was unavailable.
public sealed class WorkDayAutoSync(WorkDaySyncEngine sync,WorkDaySessionContext session,ILogger<WorkDayAutoSync> logger)
{
    private CancellationTokenSource? active;
    public void Resume()
    {
        if(active is not null)return;
        active=new();Connectivity.Current.ConnectivityChanged+=ConnectivityChanged;
        _=RunAsync(active.Token);
    }
    public void Suspend()
    {
        Connectivity.Current.ConnectivityChanged-=ConnectivityChanged;
        active?.Cancel();active?.Dispose();active=null;
    }
    private void ConnectivityChanged(object? sender,ConnectivityChangedEventArgs e)
    {
        if(e.NetworkAccess==NetworkAccess.Internet&&active is not null)_=AttemptAsync(active.Token);
    }
    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while(!ct.IsCancellationRequested)
            {
                await AttemptAsync(ct);
                await Task.Delay(TimeSpan.FromSeconds(30),ct);
            }
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { }
    }
    private async Task AttemptAsync(CancellationToken ct)
    {
        if(!session.IsAuthenticated||Connectivity.Current.NetworkAccess!=NetworkAccess.Internet)return;
        try{await sync.SynchronizeAsync(ct);}
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { }
        catch(Exception ex){logger.LogWarning(ex,"Automatic WorkDay synchronization failed; local updates retained for retry");}
    }
}
