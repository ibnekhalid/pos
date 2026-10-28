using Restaurant.Client.Core;
using Restaurant.Client.Core.Api;
using Restaurant.Client.Core.Auth;
using Restaurant.Contracts.Auth;
using Restaurant.Contracts.Inventory;

namespace Restaurant.Client.UI.Services;

/// <summary>
/// Low-stock alert for the selected branch: the count on the Inventory menu item and the list on the home
/// screen. Re-read on navigation and when orders change (sending to the kitchen uses stock), at most
/// once a minute. Only for staff who can see stock.
/// </summary>
public sealed class StockAlerts : IDisposable
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1);
    private static readonly string[] CanSee =
        [Permissions.Inventory.Receive, Permissions.Inventory.Count, Permissions.Inventory.Wastage, Permissions.Inventory.Adjust, Permissions.Reports.Inventory];

    private readonly RestaurantApi _api;
    private readonly UserContext _user;
    private readonly SessionStore _session;
    private readonly LiveUpdates _live;
    private (Guid? Branch, DateTimeOffset At) _loaded;
    private bool _loading;

    public StockAlerts(RestaurantApi api, UserContext user, SessionStore session, LiveUpdates live)
    {
        _api = api;
        _user = user;
        _session = session;
        _live = live;
        _live.OrdersChanged += OnOrdersChanged;
    }

    public IReadOnlyList<LowStockDto> Items { get; private set; } = [];

    public event Action? Changed;

    public bool Allowed => _user.CanAny(CanSee);

    /// <summary>Reads the list unless it was read for this branch less than a minute ago (force skips that).</summary>
    public async Task RefreshAsync(bool force = false)
    {
        var branch = _session.Current?.BranchId;
        if (!Allowed || branch is null)
        {
            if (Items.Count > 0) { Items = []; Changed?.Invoke(); }
            return;
        }
        if (_loading || (!force && _loaded.Branch == branch && DateTimeOffset.UtcNow - _loaded.At < MinInterval)) return;

        _loading = true;
        try
        {
            Items = await _api.GetLowStockAsync();
            _loaded = (branch, DateTimeOffset.UtcNow);
            Changed?.Invoke();
        }
        catch
        {
            // A missed refresh only delays the alert; the inventory screen always shows the truth.
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnOrdersChanged() => _ = RefreshAsync();

    public void Dispose() => _live.OrdersChanged -= OnOrdersChanged;
}
