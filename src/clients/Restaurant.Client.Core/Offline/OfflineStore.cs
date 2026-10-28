using System.Text.Json;
using Restaurant.Client.Core.Platform;
using Restaurant.Contracts.Branding;
using Restaurant.Contracts.Menu;
using Restaurant.Contracts.Orders;
using Restaurant.Contracts.Sessions;
using Restaurant.Contracts.Tables;
using Restaurant.Contracts.Tenancy;

namespace Restaurant.Client.Core.Offline;

/// <summary>What the POS needs to keep selling without the API, saved each time it loads online.</summary>
public record OfflineSnapshot(
    Guid TenantId, Guid BranchId, BusinessSessionDto Session, PosMenuDto Menu, IReadOnlyList<TableDto> Tables, IReadOnlyList<TaxRateDto> TaxRates,
    IReadOnlyList<PaymentMethodDto> PaymentMethods, BranchDto Branch, BrandingDto? Branding, TenantDto? Tenant, DateTimeOffset SavedAt);

/// <summary>An order waiting on this PC to be sent. LastError is the server's answer when it refused (it is tried again).</summary>
/// <param name="Summary">For the list on screen: "2 × Zinger Burger, 1 × Mint Margarita".</param>
public record OfflineEntry(Guid Id, Guid TenantId, Guid BranchId, OfflineOrderRequest Request, string Summary, int Attempts = 0, string? LastError = null);

/// <summary>An order that reached the server: kept for a while so the cashier can match paper tickets to order numbers.</summary>
public record SyncedEntry(string OfflineNumber, string OrderNumber, Guid OrderId, decimal Total, bool NeedsReview, string? ReviewNote, DateTimeOffset SyncedAt);

/// <summary>
/// The counter PC's offline data: the last snapshot per branch, the outbox of orders taken offline, recently
/// synced ones, and the provisional order-number counter. Everything is written to the device's own store
/// (files on the desktop) as soon as it changes, so closing the app or a power cut loses nothing that was saved.
/// </summary>
public class OfflineStore(IKeyValueStore store)
{
    private const string OutboxKey = "rp.offline.outbox";
    private const string SyncedKey = "rp.offline.synced";
    private const string CounterKey = "rp.offline.counter";
    private const int SyncedKept = 50;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<OfflineEntry>? _outbox;
    private List<SyncedEntry>? _synced;

    public event Action? Changed;

    public IReadOnlyList<OfflineEntry> Outbox => _outbox ?? [];
    public IReadOnlyList<SyncedEntry> Synced => _synced ?? [];

    public async Task InitializeAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_outbox is null)
            {
                var raw = await store.GetAsync(OutboxKey);
                _outbox = Read<List<OfflineEntry>>(raw) ?? [];
                if (_outbox.Count == 0 && !string.IsNullOrWhiteSpace(raw) && raw.Trim() != "[]")
                {
                    // Unreadable: keep the file's contents aside rather than overwrite unsent orders with an empty list.
                    await store.SetAsync($"{OutboxKey}.unreadable.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}", raw);
                }
            }
            _synced ??= Read<List<SyncedEntry>>(await store.GetAsync(SyncedKey)) ?? [];
        }
        finally
        {
            _lock.Release();
        }
    }

    // ------------------------------------------------------------ snapshot

    public Task SaveSnapshotAsync(OfflineSnapshot snapshot) => store.SetAsync(SnapshotKey(snapshot.TenantId, snapshot.BranchId), JsonSerializer.Serialize(snapshot, Json.Options));

    public async Task<OfflineSnapshot?> LoadSnapshotAsync(Guid tenantId, Guid branchId) => Read<OfflineSnapshot>(await store.GetAsync(SnapshotKey(tenantId, branchId)));

    private static string SnapshotKey(Guid tenantId, Guid branchId) => $"rp.offline.snapshot.{tenantId:N}.{branchId:N}";

    // ------------------------------------------------------------ outbox

    public async Task AddAsync(OfflineEntry entry)
    {
        await InitializeAsync();
        await ChangeAsync(() => _outbox!.Add(entry));
    }

    public Task FailedAsync(Guid id, string error) => ChangeAsync(() =>
    {
        var i = _outbox!.FindIndex(e => e.Id == id);
        if (i >= 0) _outbox[i] = _outbox[i] with { Attempts = _outbox[i].Attempts + 1, LastError = error };
    });

    public Task SyncedAsync(Guid id, SyncedEntry synced) => ChangeAsync(() =>
    {
        _outbox!.RemoveAll(e => e.Id == id);
        _synced!.Insert(0, synced);
        if (_synced.Count > SyncedKept) _synced.RemoveRange(SyncedKept, _synced.Count - SyncedKept);
    });

    /// <summary>Gives up on an order (a manager re-enters it by hand). Its paper ticket stays the only record.</summary>
    public Task DiscardAsync(Guid id) => ChangeAsync(() => _outbox!.RemoveAll(e => e.Id == id));

    /// <summary>
    /// The next provisional number for this PC: two characters from the device id, then a counter that never
    /// resets, so two PCs offline at the same time never print the same number ("K7-14").
    /// </summary>
    public async Task<string> NextNumberAsync(Guid deviceId)
    {
        await _lock.WaitAsync();
        try
        {
            var next = (int.TryParse(await store.GetAsync(CounterKey), out var n) ? n : 0) + 1;
            await store.SetAsync(CounterKey, next.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return $"{Prefix(deviceId)}-{next:00}";
        }
        finally
        {
            _lock.Release();
        }
    }

    public static string Prefix(Guid deviceId)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var bytes = deviceId.ToByteArray();
        return $"{alphabet[bytes[^1] % alphabet.Length]}{alphabet[bytes[^2] % alphabet.Length]}";
    }

    private async Task ChangeAsync(Action change)
    {
        await _lock.WaitAsync();
        try
        {
            _outbox ??= [];
            _synced ??= [];
            change();
            await store.SetAsync(OutboxKey, JsonSerializer.Serialize(_outbox, Json.Options));
            await store.SetAsync(SyncedKey, JsonSerializer.Serialize(_synced, Json.Options));
        }
        finally
        {
            _lock.Release();
        }
        Changed?.Invoke();
    }

    private static T? Read<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, Json.Options); } catch (JsonException) { return null; }
    }
}
