using Microsoft.JSInterop;

namespace Restaurant.Client.UI.Services;

/// <summary>wwwroot/restaurant-ui.js, imported once per app (works in the browser and the desktop web view).</summary>
public sealed class UiInterop(IJSRuntime js) : IAsyncDisposable
{
    private Task<IJSObjectReference>? _module;

    private Task<IJSObjectReference> Module() =>
        _module ??= js.InvokeAsync<IJSObjectReference>("import", "./_content/Restaurant.Client.UI/restaurant-ui.js").AsTask();

    public async Task<bool> PrintElementAsync(string id, int? widthMm) => await (await Module()).InvokeAsync<bool>("printElement", id, widthMm);

    public async Task BeepAsync()
    {
        try { await (await Module()).InvokeVoidAsync("beep"); } catch (JSException) { /* no audio */ }
    }

    public async Task<int> RegisterHotkeysAsync<T>(DotNetObjectReference<T> target, params string[] keys) where T : class =>
        await (await Module()).InvokeAsync<int>("registerHotkeys", target, keys);

    public async Task UnregisterHotkeysAsync(int id)
    {
        try { await (await Module()).InvokeVoidAsync("unregisterHotkeys", id); } catch (JSDisconnectedException) { /* page closing */ }
    }

    public async Task FocusAsync(string id)
    {
        try { await (await Module()).InvokeVoidAsync("focus", id); } catch (JSException) { /* gone */ }
    }

    public async Task BroadcastAsync(string channel, string json)
    {
        try { await (await Module()).InvokeVoidAsync("broadcast", channel, json); } catch (JSException) { /* no BroadcastChannel */ }
    }

    public async Task<int?> ListenAsync<T>(string channel, DotNetObjectReference<T> target) where T : class =>
        await (await Module()).InvokeAsync<int?>("listen", channel, target);

    public async Task UnlistenAsync(int id)
    {
        try { await (await Module()).InvokeVoidAsync("unlisten", id); } catch (JSDisconnectedException) { /* page closing */ }
    }

    /// <summary>A pop-up window (the customer display in the web app). False when the browser blocked it.</summary>
    public async Task<bool> OpenWindowAsync(string url, string name) => await (await Module()).InvokeAsync<bool>("openWindow", url, name);

    public async ValueTask DisposeAsync()
    {
        if (_module is { IsCompletedSuccessfully: true })
        {
            try { await _module.Result.DisposeAsync(); } catch (JSDisconnectedException) { /* closing */ }
        }
    }
}
