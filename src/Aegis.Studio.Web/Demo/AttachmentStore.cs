using Aegis.Studio.Web.Persistence;

namespace Aegis.Studio.Web.Demo;

public sealed class AttachmentStore
{
    private const string Collection = "attachments";
    private readonly PostgresDocumentStore persistence;
    private readonly string attachmentDirectory;

    public AttachmentStore(IWebHostEnvironment environment, PostgresDocumentStore persistence)
    {
        this.persistence = persistence;
        string dataDirectory = Path.Combine(environment.ContentRootPath, "App_Data");
        attachmentDirectory = Path.Combine(dataDirectory, "attachments");
        Directory.CreateDirectory(attachmentDirectory);
    }

    public IReadOnlyList<DemoAttachment> GetForConversation(Guid conversationId)
        => persistence.GetAll<DemoAttachment>(Collection).Where(x => x.ConversationId == conversationId).OrderByDescending(x => x.CreatedAt).ToList();

    public async Task<DemoAttachment> AddAsync(Guid conversationId, IFormFile file, string visibility, CancellationToken cancellationToken)
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

        DemoAttachment attachment = new(id, conversationId, originalName, storedName,
            string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
            file.Length,
            string.Equals(visibility, "Internal", StringComparison.OrdinalIgnoreCase) ? "Internal" : "External",
            DateTimeOffset.UtcNow);
        persistence.Upsert(Collection, attachment.Id, attachment);
        return attachment;
    }

    public DemoAttachment? Get(Guid id) => persistence.Get<DemoAttachment>(Collection, id);
    public string GetPhysicalPath(DemoAttachment attachment) => Path.Combine(attachmentDirectory, attachment.StoredName);
}

public sealed record DemoAttachment(Guid Id, Guid ConversationId, string FileName, string StoredName, string ContentType, long Size, string Visibility, DateTimeOffset CreatedAt);
