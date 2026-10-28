using Microsoft.Extensions.Options;
using Restaurant.Application.Common.Interfaces;

namespace Restaurant.Infrastructure.Services;

public class FileStorageSettings
{
    /// <summary>Folder for uploads (relative to the API's working directory, or absolute).</summary>
    public string RootPath { get; set; } = "uploads";

    /// <summary>Where the API serves that folder: "/uploads" or an absolute URL in production.</summary>
    public string PublicBaseUrl { get; set; } = "/uploads";
}

/// <summary>
/// Stores uploads on local disk (a Docker volume in production). Swap in a blob-storage
/// IFileStorageService later without touching callers (Tailor Book's implementation).
/// </summary>
public class LocalFileStorageService : IFileStorageService
{
    private readonly FileStorageSettings _settings;
    private readonly string _root;

    public LocalFileStorageService(IOptions<FileStorageSettings> settings)
    {
        _settings = settings.Value;
        _root = Path.GetFullPath(_settings.RootPath);
        Directory.CreateDirectory(_root);
    }

    public async Task<StoredFileResult> SaveAsync(Stream content, string fileName, string contentType, string folder, CancellationToken cancellationToken = default)
    {
        var safeFolder = string.Join('/', folder.Split('/', '\\').Where(s => s is not ("" or "." or "..")));
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var storageKey = $"{safeFolder}/{Guid.NewGuid():N}{extension}";

        var fullPath = FullPath(storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await using (var file = File.Create(fullPath))
        {
            await content.CopyToAsync(file, cancellationToken);
        }

        return new StoredFileResult(storageKey, GetPublicUrl(storageKey));
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var fullPath = FullPath(storageKey);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }
        return Task.CompletedTask;
    }

    public async Task<byte[]?> ReadAllBytesAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var fullPath = FullPath(storageKey);
        return File.Exists(fullPath) ? await File.ReadAllBytesAsync(fullPath, cancellationToken) : null;
    }

    public string GetPublicUrl(string storageKey) => $"{_settings.PublicBaseUrl.TrimEnd('/')}/{storageKey}";

    private string FullPath(string storageKey)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_root, storageKey.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(_root, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Invalid storage key.");
        }
        return fullPath;
    }
}
