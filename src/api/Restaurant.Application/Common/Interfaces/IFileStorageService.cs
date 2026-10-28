namespace Restaurant.Application.Common.Interfaces;

public record StoredFileResult(string StorageKey, string PublicUrl);

/// <summary>
/// Where uploaded files live. Local disk now; a blob-storage implementation can replace it without
/// touching callers.
/// </summary>
public interface IFileStorageService
{
    Task<StoredFileResult> SaveAsync(Stream content, string fileName, string contentType, string folder, CancellationToken cancellationToken = default);
    Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default);

    /// <summary>Returns the stored bytes, or null if nothing exists under that key anymore.</summary>
    Task<byte[]?> ReadAllBytesAsync(string storageKey, CancellationToken cancellationToken = default);

    string GetPublicUrl(string storageKey);
}
