using System.Security.Cryptography;
using Common.Storage;

namespace Aegis.Studio.Web.Versioning;

// Older local installations stored media beside the application. Recover only
// byte-identical originals; a missing real upload must never become a demo photo.
public sealed class StoredVersionRecovery(IFileStorage storage, IWebHostEnvironment environment)
{
    public async Task<Stream> OpenAsync(StudioAssetVersion version, CancellationToken cancellationToken = default)
    {
        var versions = new PrefixedFileStorage(storage, "VersionStore");
        try { return await versions.OpenReadAsync(version.StoredPath, cancellationToken); }
        catch (StorageException error) when (error.Code == "STORAGE-MISSING-001")
        {
            // An interrupted write may leave an intact provider version while
            // the current object/link is absent. Prefer those exact bytes.
            if (storage is IVersionedFileStorage history)
            {
                string key = "VersionStore/" + version.StoredPath;
                foreach (StoredFile candidate in (await storage.GetVersionsAsync(key, cancellationToken)).OrderByDescending(x => x.Version))
                {
                    if (candidate.Length != version.SizeBytes || !string.Equals(candidate.Sha256, version.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
                    Stream archived;
                    try { archived = await history.OpenVersionAsync(key, candidate.Version, cancellationToken); }
                    catch (StorageException archiveError) when (archiveError.Code is "STORAGE-VERSION-MISSING-001" or "STORAGE-INTEGRITY-001" or "STORAGE-CORRUPT-001") { continue; }
                    await using (archived)
                        await versions.StoreAsync(new(version.StoredPath, archived, version.ContentType, version.FileName, version.CreatedBy,
                            new Dictionary<string, string> { ["Application"] = "Aegis.Studio", ["VersionId"] = version.Id.ToString("D"), ["RecoveredFromStorageVersion"] = candidate.Version.ToString() }), cancellationToken);
                    return await versions.OpenReadAsync(version.StoredPath, cancellationToken);
                }
            }
            foreach (string root in new[] { Path.Combine(environment.ContentRootPath, "VersionStore"), Path.Combine(environment.ContentRootPath, "Storage", "VersionStore") })
            {
                string boundary = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
                string candidate = Path.GetFullPath(Path.Combine(root, version.StoredPath.Replace('/', Path.DirectorySeparatorChar)));
                if (!candidate.StartsWith(boundary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || !File.Exists(candidate)) continue;
                await using var source = File.OpenRead(candidate);
                if (source.Length != version.SizeBytes) continue;
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken));
                if (!string.Equals(hash, version.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
                source.Position = 0;
                await versions.StoreAsync(new(version.StoredPath, source, version.ContentType, version.FileName, version.CreatedBy,
                    new Dictionary<string, string> { ["Application"] = "Aegis.Studio", ["VersionId"] = version.Id.ToString("D"), ["RecoveredFromLegacyStorage"] = "true" }), cancellationToken);
                return await versions.OpenReadAsync(version.StoredPath, cancellationToken);
            }
            throw;
        }
    }
}
