using Restaurant.Client.Core.Api;
using Restaurant.Client.Core.Auth;
using Restaurant.Client.Core.Platform;

namespace Restaurant.Client.Core.Offline;

/// <summary>
/// Sends the outbox when the API can be reached: when the connection comes back, every 30 seconds while orders
/// are waiting, and on request ("Send now"). Only orders of the restaurant and branch signed in now are sent
/// (the API books them to the selected branch). The server skips what it already has, so sending again is safe.
/// </summary>
public sealed class OfflineSync : IDisposable
{
    private readonly RestaurantApi _api;
    private readonly SessionStore _session;
    private readonly OfflineStore _store;
    private readonly IConnectivity _connectivity;
    private readonly SemaphoreSlim _running = new(1, 1);
    private Timer? _timer;

    public OfflineSync(RestaurantApi api, SessionStore session, OfflineStore store, IConnectivity connectivity)
    {
        _api = api;
        _session = session;
        _store = store;
        _connectivity = connectivity;
    }

    /// <summary>The last attempt couldn't reach the API (the POS shows it is working offline).</summary>
    public bool Unreachable { get; private set; }

    public event Action? Changed;

    public async Task StartAsync()
    {
        if (_timer is not null) return;
        await _store.InitializeAsync();
        _connectivity.Changed += OnConnectivity;
        _timer = new Timer(_ => _ = SyncAsync(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
    }

    public IEnumerable<OfflineEntry> ForCurrentBranch() =>
        _store.Outbox.Where(e => e.TenantId == _session.Current?.TenantId && e.BranchId == _session.Current?.BranchId);

    /// <summary>Sends what's waiting for this branch, oldest first. Returns how many were booked.</summary>
    public async Task<int> SyncAsync()
    {
        if (!_session.IsSignedIn || _session.IsLocked || !ForCurrentBranch().Any()) return 0;
        if (!await _running.WaitAsync(0)) return 0;
        var booked = 0;
        try
        {
            foreach (var entry in ForCurrentBranch().ToList())
            {
                try
                {
                    var result = await _api.SyncOfflineOrderAsync(entry.Request);
                    await _store.SyncedAsync(entry.Id, new SyncedEntry(entry.Request.OfflineNumber, result.OrderNumber, result.OrderId, entry.Request.EstimatedTotal,
                        result.NeedsReview, result.ReviewNote, DateTimeOffset.UtcNow));
                    booked++;
                    Unreachable = false;
                }
                catch (NetworkException)
                {
                    Unreachable = true;
                    break; // still offline: try again later, in the same order
                }
                catch (SessionExpiredException)
                {
                    break; // someone has to sign in again; the orders wait
                }
                catch (ApiException ex)
                {
                    // Refused (no open day, a menu item deleted...): keep it with the reason and go on with the next one.
                    Unreachable = false;
                    await _store.FailedAsync(entry.Id, ex.Message);
                }
            }
        }
        finally
        {
            _running.Release();
            Changed?.Invoke();
        }
        return booked;
    }

    /// <summary>The POS tells us whether its last call reached the API.</summary>
    public void Report(bool reachable)
    {
        if (Unreachable == !reachable) return;
        Unreachable = !reachable;
        Changed?.Invoke();
        if (reachable) _ = SyncAsync();
    }

    private void OnConnectivity(bool online)
    {
        if (online) _ = SyncAsync();
    }

    public void Dispose()
    {
        _connectivity.Changed -= OnConnectivity;
        _timer?.Dispose();
    }
}
