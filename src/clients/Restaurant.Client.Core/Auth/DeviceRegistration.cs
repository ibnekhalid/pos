using System.Text.Json;
using Restaurant.Client.Core.Platform;

namespace Restaurant.Client.Core.Auth;

/// <summary>This computer's registration for counter PIN sign-in (desktop app only).</summary>
public record DeviceInfo(Guid DeviceId, string Name, Guid BranchId, Guid TenantId);

/// <summary>Per-device preferences that survive sign-out.</summary>
/// <param name="AutoLockMinutes">Lock the screen after this many idle minutes; 0 = never.</param>
/// <param name="PrintAllKitchenTickets">This PC prints every new kitchen ticket of the branch (orders from tablets and other PCs).</param>
public record DevicePrefs(int AutoLockMinutes = 5, bool PrintAllKitchenTickets = false);

/// <summary>
/// Keeps the device token (secure storage) and its details. The token is the only thing that lets
/// staff sign in with a PIN here; revoking the device on the server makes it useless.
/// </summary>
public class DeviceRegistration(ISecureStore secureStore, IKeyValueStore store, IPlatformInfo platform)
{
    private const string TokenKey = "rp.device-token";
    private const string InfoKey = "rp.device";
    private const string PrefsKey = "rp.device-prefs";

    public DeviceInfo? Info { get; private set; }
    public DevicePrefs Prefs { get; private set; } = new();
    public bool IsRegistered => Info is not null;

    /// <summary>Registered, forgotten, or preferences changed (the layout shows or hides Lock).</summary>
    public event Action? Changed;

    /// <summary>PIN sign-in is offered only by the desktop app.</summary>
    public bool Supported => platform.IsDesktop;

    public async Task InitializeAsync()
    {
        Info = Read<DeviceInfo>(await Safe(() => store.GetAsync(InfoKey)));
        Prefs = Read<DevicePrefs>(await Safe(() => store.GetAsync(PrefsKey))) ?? new DevicePrefs();
        if (Info is not null && string.IsNullOrEmpty(await GetTokenAsync()))
        {
            Info = null;
        }
    }

    public async Task<string?> GetTokenAsync() => Supported ? await Safe(() => secureStore.GetAsync(TokenKey)) : null;

    public async Task SaveAsync(string token, DeviceInfo info)
    {
        await secureStore.SetAsync(TokenKey, token);
        await store.SetAsync(InfoKey, JsonSerializer.Serialize(info, Json.Options));
        Info = info;
        Changed?.Invoke();
    }

    public async Task ForgetAsync()
    {
        await Safe(async () => { await secureStore.RemoveAsync(TokenKey); return (string?)null; });
        await Safe(async () => { await store.RemoveAsync(InfoKey); return (string?)null; });
        Info = null;
        Changed?.Invoke();
    }

    public async Task SetPrefsAsync(DevicePrefs prefs)
    {
        Prefs = prefs;
        await store.SetAsync(PrefsKey, JsonSerializer.Serialize(prefs, Json.Options));
        Changed?.Invoke();
    }

    private static T? Read<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, Json.Options); } catch { return null; }
    }

    private static async Task<string?> Safe(Func<Task<string?>> action)
    {
        try { return await action(); } catch { return null; }
    }
}
