using FluentAssertions;
using Restaurant.Client.Core.Api;
using Restaurant.Client.Core.Auth;
using Restaurant.Client.Core.Offline;
using Restaurant.Client.Core.Platform;
using Restaurant.Contracts.Auth;
using Restaurant.Contracts.Orders;
using Restaurant.Contracts.Sessions;
using Restaurant.Tests.Integration.Infrastructure;

namespace Restaurant.Tests.Integration;

/// <summary>The desktop app's own sync code (OfflineStore + OfflineSync + the typed API client) against the real API.</summary>
public class OfflineClientSyncTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private sealed class MemoryStore : IKeyValueStore, ISecureStore
    {
        private readonly Dictionary<string, string> _values = [];
        public Task<string?> GetAsync(string key) => Task.FromResult(_values.GetValueOrDefault(key));
        public Task SetAsync(string key, string value) { _values[key] = value; return Task.CompletedTask; }
        public Task RemoveAsync(string key) { _values.Remove(key); return Task.CompletedTask; }
    }

    private sealed class AlwaysOnline : IConnectivity
    {
        public bool IsOnline => true;
        public event Action<bool>? Changed { add { } remove { } }
        public Task InitializeAsync() => Task.CompletedTask;
    }

    [Fact]
    public async Task The_outbox_is_sent_in_order_and_a_refused_order_waits_with_its_reason()
    {
        var pos = await factory.SetUpRestaurantAsync();
        var me = await pos.Owner.GetAsync<MeDto>("/api/me");
        var day = await pos.Owner.GetAsync<BusinessSessionDto>("/api/sessions/current");

        // The client as the desktop app builds it, signed in as the owner on the main branch.
        var memory = new MemoryStore();
        var session = new SessionStore(memory);
        await session.ApplySignInAsync(pos.Owner.Auth, rememberMe: false);
        await session.SetBranchAsync(pos.Owner.BranchId);
        var api = new RestaurantApi(new ApiClient(factory.CreateClient(), session), session);
        var store = new OfflineStore(memory);
        var sync = new OfflineSync(api, session, store, new AlwaysOnline());

        var device = Guid.NewGuid();
        OfflineEntry Entry(string number, Guid menuItem) => new(Guid.NewGuid(), pos.Owner.TenantId, pos.Owner.BranchId,
            new OfflineOrderRequest(pos.StandardOrder() with { Lines = [new(menuItem, null, 1, [], null)] }, number, DateTimeOffset.UtcNow.AddMinutes(-5),
                me.UserId, day.Id, 348, [new(Guid.NewGuid(), pos.CashMethod, 348, 500, null)], null), "1 × Mint Margarita");

        var good = Entry(await store.NextNumberAsync(device), pos.Drink);
        var bad = Entry(await store.NextNumberAsync(device), Guid.NewGuid()); // an item deleted from the menu meanwhile
        await store.AddAsync(good);
        await store.AddAsync(bad);

        (await sync.SyncAsync()).Should().Be(1);
        sync.Unreachable.Should().BeFalse();
        store.Synced.Should().ContainSingle().Which.Should().Match<SyncedEntry>(s => s.OfflineNumber == good.Request.OfflineNumber && s.OrderNumber == "001" && !s.NeedsReview);
        store.Outbox.Should().ContainSingle().Which.Should().Match<OfflineEntry>(e => e.Id == bad.Id && e.Attempts == 1 && e.LastError != null);

        var order = await pos.Owner.GetAsync<OrderDto>($"/api/orders/{store.Synced[0].OrderId}");
        order.OfflineNumber.Should().Be(good.Request.OfflineNumber);
        order.Payments.Single().Change.Should().Be(152);

        // Sending again books nothing twice and keeps the refused one waiting.
        (await sync.SyncAsync()).Should().Be(0);
        store.Outbox.Single().Attempts.Should().Be(2);
        (await pos.Owner.GetAsync<List<OrderSummaryDto>>("/api/orders?scope=today")).Should().ContainSingle();
    }
}
