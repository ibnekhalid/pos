using Restaurant.Client.Core;
using Restaurant.Client.Core.Api;
using Restaurant.Client.Core.Auth;
using Restaurant.Contracts.Enums;

namespace Restaurant.Client.UI.Services;

/// <summary>
/// On the one counter PC set to "print every kitchen ticket": prints each new ticket of the branch that hasn't
/// been printed anywhere yet (orders from waiters' tablets and phones on the web app, or PCs without a kitchen printer).
/// It waits a few seconds after a ticket is sent, so the PC that sent it prints its own first, and never prints
/// the same ticket twice. Woken by the live ticket updates, with a check every 30 seconds as a backstop.
/// </summary>
public sealed class KitchenAutoPrint : IDisposable
{
    private static readonly TimeSpan SenderFirst = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TooOld = TimeSpan.FromHours(2);

    private readonly RestaurantApi _api;
    private readonly PrintService _printer;
    private readonly DeviceRegistration _device;
    private readonly SessionStore _session;
    private readonly UserContext _user;
    private readonly LiveUpdates _live;
    private readonly HashSet<Guid> _done = [];
    private readonly SemaphoreSlim _running = new(1, 1);
    private Timer? _timer;

    public KitchenAutoPrint(RestaurantApi api, PrintService printer, DeviceRegistration device, SessionStore session, UserContext user, LiveUpdates live)
    {
        _api = api;
        _printer = printer;
        _device = device;
        _session = session;
        _user = user;
        _live = live;
    }

    /// <summary>The last printing problem, for the settings screen ("Kitchen printer offline...").</summary>
    public string? LastError { get; private set; }

    private bool Enabled => _device.Supported && _device.Prefs.PrintAllKitchenTickets && _printer.SilentPrinting
        && _session.IsSignedIn && !_session.IsLocked && _user.CanAny(Contracts.Auth.Permissions.Kitchen.View, Contracts.Auth.Permissions.Payments.PrintReceipts);

    public void Start()
    {
        if (_timer is not null) return;
        _live.TicketsChanged += OnTickets;
        _live.Reconnected += OnTickets;
        _timer = new Timer(_ => _ = RunAsync(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30));
        _ = _live.EnsureConnectedAsync();
    }

    private void OnTickets() => _ = RunAsync(SenderFirst);

    private async Task RunAsync(TimeSpan? wait = null)
    {
        if (!Enabled) return;
        if (wait is { } delay) await Task.Delay(delay);
        if (!await _running.WaitAsync(0)) return;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var due = (await _api.GetKitchenTicketsAsync(null))
                .Where(t => !t.Printed && !_done.Contains(t.Id) && t.Status == KitchenTicketStatus.New
                    && now - t.SentAt >= SenderFirst && now - t.SentAt < TooOld)
                .OrderBy(t => t.SentAt)
                .ToList();
            foreach (var ticket in due)
            {
                var error = await _printer.PrintAsync(ticket.OrderId, PrintKind.KitchenTicket, ticket.Id);
                LastError = error;
                if (error is not null) break; // printer offline: try again on the next round
                _done.Add(ticket.Id);
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        finally
        {
            _running.Release();
        }
    }

    public void Dispose()
    {
        _live.TicketsChanged -= OnTickets;
        _live.Reconnected -= OnTickets;
        _timer?.Dispose();
    }
}
