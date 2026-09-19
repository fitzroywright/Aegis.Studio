using Aegis.Studio.WorkDay.Core;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Aegis.Studio.WorkDay;

public sealed class WorkDayUploadService(WorkDayLocalStore store,WorkDayApiClient api)
{
    private readonly SemaphoreSlim gate=new(1,1);

    public async Task<LocalUpload?> PickAndQueueAsync(Guid assignmentId,CancellationToken ct=default)
    {
        FileResult? picked=await FilePicker.Default.PickAsync(new PickOptions{PickerTitle="Select file for this session"});
        if(picked is null)return null;
        string folder=Path.Combine(FileSystem.AppDataDirectory,"workday-uploads");
        Directory.CreateDirectory(folder);
        Guid uploadId=Guid.NewGuid();
        string safe=Path.GetFileName(picked.FileName);
        string local=Path.Combine(folder,uploadId.ToString("N")+Path.GetExtension(safe));
        await using(Stream source=await picked.OpenReadAsync())
        await using(FileStream dest=File.Create(local))
            await source.CopyToAsync(dest,ct);
        FileInfo info=new(local);
        LocalUpload item=new(){UploadId=uploadId,AssignmentId=assignmentId,LocalPath=local,FileName=safe,ContentType=string.IsNullOrWhiteSpace(picked.ContentType)?"application/octet-stream":picked.ContentType,SizeBytes=info.Length,CreatedAtUtc=DateTimeOffset.UtcNow};
        await store.QueueUploadAsync(item);
        return item;
    }

    public async Task ProcessAsync(CancellationToken ct=default)
    {
        if(!await gate.WaitAsync(0,ct))return;
        try
        {
            foreach(LocalUpload upload in await store.PendingUploadsAsync())
            {
                if(!File.Exists(upload.LocalPath)){upload.State=nameof(LocalUploadState.Failed);upload.LastError="Local file is missing.";await store.SaveUploadAsync(upload);continue;}
                upload.State=nameof(LocalUploadState.Uploading);upload.AttemptCount++;upload.LastAttemptUtc=DateTimeOffset.UtcNow;await store.SaveUploadAsync(upload);
                try
                {
                    UploadResultDto result=await api.UploadAsync(upload,ct);
                    upload.ServerAttachmentId=result.AttachmentId;upload.State=nameof(LocalUploadState.Uploaded);upload.LastError=null;
                    await store.SaveUploadAsync(upload);
                    try{File.Delete(upload.LocalPath);}catch{}
                }
                catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException or IOException)
                {
                    upload.State=nameof(LocalUploadState.Retrying);upload.LastError=ex.Message;await store.SaveUploadAsync(upload);break;
                }
            }
        }
        finally{gate.Release();}
    }
}

public sealed record UploadResultDto(Guid UploadId,Guid AttachmentId,Guid AssignmentId,string FileName,long SizeBytes,string StorageStatus,DateTimeOffset AcknowledgedAtUtc);
