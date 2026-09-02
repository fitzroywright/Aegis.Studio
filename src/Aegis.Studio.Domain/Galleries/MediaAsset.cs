namespace Aegis.Studio.Domain.Galleries;
public sealed class MediaAsset
{
    private MediaAsset() { }
    public MediaAsset(Guid galleryId,string fileName,string contentType,long sizeBytes,string storageKey,string uploadedBy){Id=Guid.NewGuid();GalleryId=galleryId;FileName=fileName.Trim();ContentType=contentType.Trim();SizeBytes=sizeBytes;StorageKey=storageKey;UploadedBy=uploadedBy.Trim();Version=1;CreatedUtc=DateTimeOffset.UtcNow;UpdatedUtc=CreatedUtc;}
    public Guid Id{get;private set;} public Guid GalleryId{get;private set;} public string FileName{get;private set;}=string.Empty; public string ContentType{get;private set;}=string.Empty; public long SizeBytes{get;private set;} public string StorageKey{get;private set;}=string.Empty; public string UploadedBy{get;private set;}=string.Empty; public int Version{get;private set;} public DateTimeOffset CreatedUtc{get;private set;} public DateTimeOffset UpdatedUtc{get;private set;}
    public void MoveTo(Guid galleryId){GalleryId=galleryId;UpdatedUtc=DateTimeOffset.UtcNow;} public void Replace(string fileName,string contentType,long sizeBytes,string storageKey,string uploadedBy){FileName=fileName.Trim();ContentType=contentType.Trim();SizeBytes=sizeBytes;StorageKey=storageKey;UploadedBy=uploadedBy.Trim();Version++;UpdatedUtc=DateTimeOffset.UtcNow;}
}
