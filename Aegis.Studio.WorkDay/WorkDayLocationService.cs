namespace Aegis.Studio.WorkDay;

public sealed class WorkDayLocationService(WorkDayLocalStore store,WorkDayDeviceIdentity device)
{
    private CancellationTokenSource? trackingCts;
    private bool appSuspended;
    public string PermissionState{get;private set;}="Unknown";
    public DateTimeOffset? LastSamplingAttemptUtc{get;private set;}
    public DateTimeOffset? LastObservationUtc{get;private set;}
    public bool TrackingActive=>trackingCts is not null;
    public string SamplingState=>!TrackingActive?"Inactive":appSuspended?"SuspendedByOS":LastObservationUtc is null?"WaitingForEvidence":"Active";

    public async Task<LocalLocationObservation?> CaptureAsync(Guid? assignmentId,Guid? workSessionId,CancellationToken ct=default)
    {
        try
        {
            LastSamplingAttemptUtc=DateTimeOffset.UtcNow;
            PermissionStatus permission=await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
            if(permission!=PermissionStatus.Granted) permission=await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
            PermissionState=permission.ToString();
            if(permission!=PermissionStatus.Granted)return null;
            Location? l=await Geolocation.Default.GetLocationAsync(new GeolocationRequest(GeolocationAccuracy.Medium,TimeSpan.FromSeconds(10)),ct);
            if(l is null)return null;
            LocalLocationObservation obs=new(){ObservationId=Guid.NewGuid(),AssignmentId=assignmentId,WorkSessionId=workSessionId,Latitude=l.Latitude,Longitude=l.Longitude,AccuracyMetres=l.Accuracy,ObservedAtUtc=l.Timestamp,Source="Device"};
            await store.SaveLocationAsync(obs);LastObservationUtc=obs.ObservedAtUtc;return obs;
        }
        catch(FeatureNotSupportedException){PermissionState="Unavailable";return null;}
        catch(PermissionException){PermissionState="Denied";return null;}
        catch(TaskCanceledException){return null;}
    }

    public void StartBatteryConsciousTracking(Func<Task<Guid?>> currentSession,Func<Task<Guid?>> currentAssignment,TimeSpan? interval=null)
    {
        if(trackingCts is not null)return;
        trackingCts=new CancellationTokenSource();
        CancellationToken token=trackingCts.Token;
        _=Task.Run(async()=>{
            using PeriodicTimer timer=new(interval??TimeSpan.FromMinutes(15));
            while(!token.IsCancellationRequested)
            {
                Guid? session=await currentSession(); if(session is null)break;
                await CaptureAsync(await currentAssignment(),session,token);
                if(!await timer.WaitForNextTickAsync(token))break;
            }
        },token);
    }

    public void NotifyAppSuspended(){appSuspended=true;}
    public void NotifyAppResumed(){appSuspended=false;}
    public void StopTracking(){trackingCts?.Cancel();trackingCts?.Dispose();trackingCts=null;appSuspended=false;}
}
