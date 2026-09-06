using System.Collections.Concurrent;
using System.Text.Json;

namespace Aegis.Studio.Web.Demo;

public sealed class AttachmentStore
{
    private readonly ConcurrentDictionary<Guid, DemoAttachment> attachments = new();
    private readonly object persistenceLock = new();
    private readonly string attachmentDirectory;
    private readonly string metadataFile;

    public AttachmentStore(IWebHostEnvironment environment)
    {
        string dataDirectory = Path.Combine(environment.ContentRootPath, "App_Data");
        attachmentDirectory = Path.Combine(dataDirectory, "attachments");
        metadataFile = Path.Combine(dataDirectory, "attachments.json");

        Directory.CreateDirectory(attachmentDirectory);
        Load();
    }

    public IReadOnlyList<DemoAttachment> GetForConversation(Guid conversationId)
    {
        return attachments.Values
            .Where(x => x.ConversationId == conversationId)
            .OrderByDescending(x => x.CreatedAt)
            .ToList();
    }

    public async Task<DemoAttachment> AddAsync(
        Guid conversationId,
        IFormFile file,
        string visibility,
        CancellationToken cancellationToken)
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

        DemoAttachment attachment = new(
            id,
            conversationId,
            originalName,
            storedName,
            string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
            file.Length,
            string.Equals(visibility, "Internal", StringComparison.OrdinalIgnoreCase) ? "Internal" : "External",
            DateTimeOffset.UtcNow);

        attachments[id] = attachment;
        Save();
        return attachment;
    }

    public DemoAttachment? Get(Guid id)
    {
        attachments.TryGetValue(id, out DemoAttachment? attachment);
        return attachment;
    }

    public string GetPhysicalPath(DemoAttachment attachment)
    {
        return Path.Combine(attachmentDirectory, attachment.StoredName);
    }

    private void Load()
    {
        if (!File.Exists(metadataFile))
        {
            return;
        }

        try
        {
            string json = File.ReadAllText(metadataFile);
            List<DemoAttachment>? stored = JsonSerializer.Deserialize<List<DemoAttachment>>(json);
            if (stored is null)
            {
                return;
            }

            foreach (DemoAttachment attachment in stored)
            {
                attachments[attachment.Id] = attachment;
            }
        }
        catch (JsonException)
        {
            // A corrupt demo metadata file must not prevent the application from starting.
        }
    }

    private void Save()
    {
        lock (persistenceLock)
        {
            string json = JsonSerializer.Serialize(
                attachments.Values.OrderBy(x => x.CreatedAt).ToList(),
                new JsonSerializerOptions { WriteIndented = true });
            string temporaryFile = metadataFile + ".tmp";
            File.WriteAllText(temporaryFile, json);
            File.Move(temporaryFile, metadataFile, true);
        }
    }
}

public sealed record DemoAttachment(
    Guid Id,
    Guid ConversationId,
    string FileName,
    string StoredName,
    string ContentType,
    long Size,
    string Visibility,
    DateTimeOffset CreatedAt);
