using System.Security.Cryptography;
using System.Text;
using Restaurant.Client.Core.Platform;
using IConnectivity = Restaurant.Client.Core.Platform.IConnectivity;

namespace Restaurant.Desktop.Platform;

// Platform services with no Windows-only code (kept apart from the Windows ones in DesktopPlatformInfo.cs).

/// <summary>
/// One file per key under the app's data folder (Tailor Book's store). File names are hashes, because
/// keys contain characters like ':' and '/'. Write-then-rename, so a crash never leaves half a file.
/// </summary>
public class FileKeyValueStore : IKeyValueStore
{
    private readonly string _root;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public FileKeyValueStore() : this(Path.Combine(FileSystem.AppDataDirectory, "kv"))
    {
    }

    protected FileKeyValueStore(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    public async Task<string?> GetAsync(string key)
    {
        var path = PathFor(key);
        await _lock.WaitAsync();
        try
        {
            return File.Exists(path) ? Decode(await File.ReadAllBytesAsync(path)) : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetAsync(string key, string value)
    {
        var path = PathFor(key);
        await _lock.WaitAsync();
        try
        {
            // Flushed to the disk before the rename: a power cut leaves the old file or the new one, never half of one
            // (the offline outbox lives here).
            await using (var file = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await file.WriteAsync(Encode(value));
                file.Flush(flushToDisk: true);
            }
            File.Move(path + ".tmp", path, overwrite: true);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveAsync(string key)
    {
        await _lock.WaitAsync();
        try
        {
            File.Delete(PathFor(key));
        }
        finally
        {
            _lock.Release();
        }
    }

    protected virtual byte[] Encode(string value) => Encoding.UTF8.GetBytes(value);
    protected virtual string? Decode(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    private string PathFor(string key) => Path.Combine(_root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".dat");
}

public class MauiConnectivity : IConnectivity
{
    public bool IsOnline { get; private set; } = true;
    public event Action<bool>? Changed;

    public Task InitializeAsync()
    {
        IsOnline = Connectivity.Current.NetworkAccess == NetworkAccess.Internet;
        Connectivity.Current.ConnectivityChanged += (_, e) =>
        {
            IsOnline = e.NetworkAccess == NetworkAccess.Internet;
            Changed?.Invoke(IsOnline);
        };
        return Task.CompletedTask;
    }
}
