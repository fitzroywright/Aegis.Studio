using Aegis.Studio.Web.Persistence;

namespace Aegis.Studio.Web.Storage;

public sealed class AttachmentStore
{
    private const string Collection = "attachments";
    private readonly PostgresDocumentStore persistence;
    private readonly string attachmentDirectory;

    public AttachmentStore(IWebHostEnvironment environment, IConfiguration configuration, PostgresDocumentStore persistence)
    {
        this.persistence = persistence;
        string configuredPath = configuration["Storage:AttachmentsPath"] ?? string.Empty;
        attachmentDirectory = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(environment.ContentRootPath, "Storage", "attachments")
            : Path.GetFullPath(configuredPath);
        Directory.CreateDirectory(attachmentDirectory);
    }

    public IReadOnlyList<StudioAttachment> GetForConversation(Guid conversationId)
        => persistence.GetAll<StudioAttachment>(Collection).Where(x => x.ConversationId == conversationId).OrderByDescending(x => x.CreatedAt).ToList();

    public async Task<StudioAttachment> AddAsync(Guid conversationId, IFormFile file, string visibility, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        string originalName = Path.GetFileName(file.FileName);
        string extension = Path.GetExtension(originalName);
        Guid id = Guid.NewGuid();
        string storedName = id.ToString("N") + extension;
        string fullPath = Path.Combine(attachmentDirectory, storedName);

        await using (FileStream stream = File.Create(fullPath))
        {
            await file.CopyToAsync(stream, cancellationToken);
        }

        StudioAttachment attachment = new(id, conversationId, originalName, storedName,
            string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
            file.Length,
            string.Equals(visibility, "Internal", StringComparison.OrdinalIgnoreCase) ? "Internal" : "External",
            DateTimeOffset.UtcNow);
        persistence.Upsert(Collection, attachment.Id, attachment);
        return attachment;
    }

    public StudioAttachment? Get(Guid id) => persistence.Get<StudioAttachment>(Collection, id);
    public string GetPhysicalPath(StudioAttachment attachment) => Path.Combine(attachmentDirectory, attachment.StoredName);
}

public sealed record StudioAttachment(Guid Id, Guid ConversationId, string FileName, string StoredName, string ContentType, long Size, string Visibility, DateTimeOffset CreatedAt);
