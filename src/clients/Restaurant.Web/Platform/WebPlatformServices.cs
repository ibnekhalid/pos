using Microsoft.JSInterop;
using Restaurant.Client.Core.Platform;

namespace Restaurant.Web.Platform;

/// <summary>Lazily imports wwwroot/js/restaurant.js once for all browser platform services.</summary>
public class BrowserInterop(IJSRuntime js)
{
    private Task<IJSObjectReference>? _module;

    public async ValueTask<T> InvokeAsync<T>(string name, params object?[] args) => await (await Module()).InvokeAsync<T>(name, args);

    public async ValueTask InvokeVoidAsync(string name, params object?[] args) => await (await Module()).InvokeVoidAsync(name, args);

    private Task<IJSObjectReference> Module() => _module ??= js.InvokeAsync<IJSObjectReference>("import", "./js/restaurant.js").AsTask();
}

/// <summary>Preferences (language) in localStorage — small values only; nothing is cached offline (PLAN.md D8).</summary>
public class LocalStorageStore(BrowserInterop js) : IKeyValueStore
{
    public async Task<string?> GetAsync(string key) => await js.InvokeAsync<string?>("lsGet", key);
    public async Task SetAsync(string key, string value) => await js.InvokeVoidAsync("lsSet", key, value);
    public async Task RemoveAsync(string key) => await js.InvokeVoidAsync("lsRemove", key);
}

/// <summary>
/// localStorage for the remembered session. Browsers have no keychain: SessionStore writes here
/// only when "remember me" is on, and the server limits other sessions to 12 hours.
/// </summary>
public class LocalStorageSecureStore(BrowserInterop js) : ISecureStore
{
    public async Task<string?> GetAsync(string key) => await js.InvokeAsync<string?>("lsGet", key);
    public async Task SetAsync(string key, string value) => await js.InvokeVoidAsync("lsSet", key, value);
    public async Task RemoveAsync(string key) => await js.InvokeVoidAsync("lsRemove", key);
}

public sealed class BrowserConnectivity(BrowserInterop js) : IConnectivity, IDisposable
{
    private DotNetObjectReference<BrowserConnectivity>? _ref;

    public bool IsOnline { get; private set; } = true;
    public event Action<bool>? Changed;

    public async Task InitializeAsync()
    {
        IsOnline = await js.InvokeAsync<bool>("isOnline");
        _ref = DotNetObjectReference.Create(this);
        await js.InvokeVoidAsync("watchConnectivity", _ref);
    }

    [JSInvokable]
    public void OnConnectivityChanged(bool online)
    {
        IsOnline = online;
        Changed?.Invoke(online);
    }

    public void Dispose() => _ref?.Dispose();
}

/// <summary>Browser full screen (counter PCs running the web app in Chrome or Edge).</summary>
public class BrowserFullScreen(BrowserInterop js) : IWindowService
{
    public bool SupportsFullScreen => true;
    public async Task ToggleFullScreenAsync() => await js.InvokeVoidAsync("toggleFullScreen");
}

public class WebPlatformInfo : IPlatformInfo
{
    public string Name => "Web";
    public bool IsDesktop => false;
    public string DeviceName => "Browser";
}
