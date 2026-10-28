using Aegis.Studio.WorkDay.Core;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Aegis.Studio.WorkDay;

public sealed class WorkDayUploadService(WorkDayLocalStore store,WorkDayApiClient api)
{
    private readonly SemaphoreSlim gate=new(1,1);
    private CancellationTokenSource? active;
    private bool paused;
    public event Action<PhotoTransferProgress>? Progress;
    public void Pause(){paused=true;active?.Cancel();}
    public async Task ResumeAssignmentAsync(Guid assignment,CancellationToken ct=default)
    {foreach(var item in await store.UploadsForAssignmentAsync(assignment))if(item.State==nameof(LocalUploadState.Paused)){item.State=nameof(LocalUploadState.Retrying);await store.SaveUploadAsync(item);}await ProcessAsync(ct);}

    public async Task CancelAssignmentAsync(Guid assignment,CancellationToken ct=default)
    {
        Pause();await gate.WaitAsync(ct);
        try
        {
            foreach(var item in await store.UploadsForAssignmentAsync(assignment))
            {
                if(item.State is nameof(LocalUploadState.Uploaded) or nameof(LocalUploadState.Cancelled))continue;
                await api.CancelUploadAsync(item,ct);
                item.State=nameof(LocalUploadState.Cancelled);await store.SaveUploadAsync(item);
                if(File.Exists(item.LocalPath))File.Delete(item.LocalPath);
            }
        }
        finally{gate.Release();}
    }

    public async Task<LocalUpload?> PickAndQueueAsync(Guid assignmentId,CancellationToken ct=default)
    {
        FileResult? picked=await FilePicker.Default.PickAsync(new PickOptions{PickerTitle="Select file for this session"});
        if(picked is null)return null;
        string folder=Path.Combine(FileSystem.AppDataDirectory,"workday-uploads");
        Directory.CreateDirectory(folder);
        Guid uploadId=Guid.NewGuid();
        string safe=Path.GetFileName(picked.FileName);
        string local=Path.Combine(folder,uploadId.ToString("N")+Path.GetExtension(safe));
        try
        {
            await using Stream source=await picked.OpenReadAsync();
            await using var dest=new FileStream(local,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,FileOptions.Asynchronous);
            byte[] buffer=new byte[65536];long size=0;int count;
            while((count=await source.ReadAsync(buffer,ct))>0)
            {
                size+=count;if(size>ResumablePhotoTransfer.MaximumBytes)throw new InvalidOperationException("The photograph exceeds the 512 MiB upload limit.");
                await dest.WriteAsync(buffer.AsMemory(0,count),ct);
            }
            if(size==0)throw new InvalidOperationException("The photograph is empty.");
            await dest.FlushAsync(ct);
        }
        catch{if(File.Exists(local))File.Delete(local);throw;}
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
                    paused=false;active?.Dispose();active=CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var progress=new Progress<PhotoTransferProgress>(value=>Progress?.Invoke(value));
                    UploadResultDto result=await api.UploadAsync(upload,active.Token,progress);
                    upload.ServerAttachmentId=result.AttachmentId;upload.State=nameof(LocalUploadState.Uploaded);upload.LastError=null;
                    await store.SaveUploadAsync(upload);
                    try{File.Delete(upload.LocalPath);}catch{}
                }
                catch(HttpRequestException ex) when(ex.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Conflict)
                {
                    upload.State=nameof(LocalUploadState.Conflict);upload.LastError=ex.Message;await store.SaveUploadAsync(upload);
                }
                catch(InvalidOperationException ex)
                {upload.State=nameof(LocalUploadState.Conflict);upload.LastError=ex.Message;await store.SaveUploadAsync(upload);}
                catch(Exception ex) when(ex is HttpRequestException or OperationCanceledException or IOException)
                {
                    upload.State=paused?nameof(LocalUploadState.Paused):nameof(LocalUploadState.Retrying);upload.LastError=ex.Message;await store.SaveUploadAsync(upload);break;
                }
            }
        }
        finally{active?.Dispose();active=null;gate.Release();}
    }
}

public sealed record UploadResultDto(Guid UploadId,Guid AttachmentId,Guid AssignmentId,string FileName,long SizeBytes,string StorageStatus,DateTimeOffset AcknowledgedAtUtc,string? Sha256=null);
