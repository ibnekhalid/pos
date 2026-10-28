using Microsoft.AspNetCore.SignalR.Client;
using Restaurant.Client.Core.Api;
using Restaurant.Client.Core.Auth;
using Restaurant.Contracts.Live;

namespace Restaurant.Client.Core;

/// <summary>
/// The SignalR connection that tells screens "orders/tickets changed at your branch". Only a hint: the
/// screens re-read from the API (and re-read on every reconnect), so nothing is lost if a message is.
/// </summary>
public sealed class LiveUpdates : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly SessionStore _session;
    private readonly ApiClient _api;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private HubConnection? _connection;
    private readonly HashSet<Guid> _joined = [];

    public LiveUpdates(HttpClient http, SessionStore session, ApiClient api)
    {
        _http = http;
        _session = session;
        _api = api;
        session.SessionEnded += StopAsync;
    }

    public event Action? OrdersChanged;
    public event Action? TicketsChanged;

    /// <summary>The connection came back: everything may have changed.</summary>
    public event Action? Reconnected;

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    /// <summary>Connects (once) and joins the selected branch. Failures are swallowed: screens also refresh on their own.</summary>
    public async Task EnsureConnectedAsync()
    {
        if (!_session.IsSignedIn || _session.Current?.BranchId is not { } branchId) return;

        await _lock.WaitAsync();
        try
        {
            if (_connection is null)
            {
                _connection = new HubConnectionBuilder()
                    .WithUrl(new Uri(_http.BaseAddress!, LiveEvents.HubPath), options => options.AccessTokenProvider = _api.GetAccessTokenAsync)
                    .WithAutomaticReconnect()
                    .Build();
                _connection.On<Guid>(LiveEvents.OrdersChanged, id => { if (id == _session.Current?.BranchId) OrdersChanged?.Invoke(); });
                _connection.On<Guid>(LiveEvents.TicketsChanged, id => { if (id == _session.Current?.BranchId) TicketsChanged?.Invoke(); });
                _connection.Reconnected += async _ =>
                {
                    _joined.Clear();
                    await EnsureConnectedAsync();
                    Reconnected?.Invoke();
                };
            }

            if (_connection.State == HubConnectionState.Disconnected)
            {
                _joined.Clear();
                await _connection.StartAsync();
            }
            if (_connection.State == HubConnectionState.Connected && _joined.Add(branchId))
            {
                await _connection.InvokeAsync(LiveEvents.JoinBranch, branchId);
            }
        }
        catch
        {
            // Offline or not allowed: the screens' own refresh keeps working.
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task StopAsync()
    {
        if (_connection is null) return;
        var connection = _connection;
        _connection = null;
        _joined.Clear();
        try { await connection.DisposeAsync(); } catch { /* already closed */ }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
