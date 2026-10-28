using FluentAssertions;
using Restaurant.Client.Core.Offline;
using Restaurant.Client.Core.Platform;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Orders;
using Restaurant.Contracts.Tenancy;
using Restaurant.Domain.Pricing;

namespace Restaurant.Tests.Unit;

/// <summary>The counter PC's offline estimate equals the server's pricing; the outbox numbers and keeps orders.</summary>
public class OfflineTests
{
    private static BranchDto Branch(decimal serviceCharge = 0, OrderTypes scTypes = OrderTypes.DineIn, RoundingMode rounding = RoundingMode.None) =>
        new(Guid.NewGuid(), "Main", null, null, null, "", true,
            new BranchSettingsDto("Asia/Karachi", 6, serviceCharge, scTypes, rounding, SessionScope.PerBranch, 500, InsufficientStockBehavior.Allow, 15));

    private static TaxRateDto Rate(string name, decimal percent, bool inclusive = false, TaxPaymentScope scope = TaxPaymentScope.Any,
        OrderTypes types = OrderTypes.All, bool active = true) =>
        new(Guid.NewGuid(), name, percent, inclusive, types, scope, null, active);

    public static TheoryData<OrderType, bool, decimal, RoundingMode, string> Cases => new()
    {
        { OrderType.DineIn, true, 10, RoundingMode.None, "excl16" },
        { OrderType.Takeaway, true, 10, RoundingMode.NearestRupee, "incl17" },
        { OrderType.DineIn, false, 5, RoundingMode.NearestFive, "cash16card5" },
        { OrderType.DineIn, true, 0, RoundingMode.NearestTen, "two-inclusive" },
        { OrderType.Delivery, false, 7.5m, RoundingMode.None, "mixed" },
    };

    private static TaxRateDto[] Rates(string set) => set switch
    {
        "excl16" => [Rate("GST", 16)],
        "incl17" => [Rate("PST", 17, inclusive: true)],
        "cash16card5" => [Rate("GST cash", 16, scope: TaxPaymentScope.CashOnly), Rate("GST card", 5, scope: TaxPaymentScope.NonCashOnly)],
        "two-inclusive" => [Rate("A", 10, inclusive: true), Rate("B", 6.5m, inclusive: true)],
        _ => [Rate("Incl", 5, inclusive: true), Rate("Excl", 8), Rate("Takeaway only", 3, types: OrderTypes.Takeaway), Rate("Off", 9, active: false)]
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_offline_estimate_is_the_servers_price(OrderType type, bool cash, decimal serviceCharge, RoundingMode rounding, string rates)
    {
        PricingLine[] lines = [new(550, 100, 2), new(333.33m, 0, 3), new(90, 15.5m, 1)];
        var branch = Branch(serviceCharge, OrderTypes.DineIn | OrderTypes.Delivery, rounding);
        var taxRates = Rates(rates);

        var estimate = OfflineEstimate.Price(lines.Select(l => l.Total), type, cash, taxRates, branch);
        var server = OrderPricer.Price(new PricingInput(type, lines, null, null, serviceCharge,
            serviceCharge > 0 && (OrderTypes.DineIn | OrderTypes.Delivery).HasFlag((OrderTypes)(int)type),
            taxRates.Where(r => r.IsActive && r.AppliesTo.HasFlag((OrderTypes)(int)type))
                .Select(r => new PricingTax(r.Name, r.Percent, r.IsInclusive, r.AppliesTo, r.PaymentScope)).ToList(),
            cash ? TaxTender.Cash : TaxTender.NonCash, rounding));

        estimate.Subtotal.Should().Be(server.Subtotal);
        estimate.ServiceCharge.Should().Be(server.ServiceCharge);
        estimate.Taxes.Select(t => (t.Name, t.Amount)).Should().Equal(server.Taxes.Select(t => (t.Name, t.Amount)));
        estimate.Rounding.Should().Be(server.Rounding);
        estimate.Total.Should().Be(server.Total);
    }

    private sealed class MemoryStore : IKeyValueStore
    {
        public readonly Dictionary<string, string> Values = [];
        public Task<string?> GetAsync(string key) => Task.FromResult(Values.GetValueOrDefault(key));
        public Task SetAsync(string key, string value) { Values[key] = value; return Task.CompletedTask; }
        public Task RemoveAsync(string key) { Values.Remove(key); return Task.CompletedTask; }
    }

    private static OfflineEntry Entry(string number) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        new OfflineOrderRequest(new CreateOrderRequest(Guid.NewGuid(), OrderType.Takeaway, null, null, null, null, null, [], null, true),
            number, DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid(), 100, [], null), "1 × Tea");

    [Fact]
    public async Task Numbers_never_repeat_on_a_pc_and_the_outbox_survives_a_restart()
    {
        var store = new MemoryStore();
        var device = Guid.NewGuid();
        var outbox = new OfflineStore(store);

        var first = await outbox.NextNumberAsync(device);
        var second = await new OfflineStore(store).NextNumberAsync(device); // after a restart
        first.Should().MatchRegex("^[A-Z2-9]{2}-01$");
        second.Should().EndWith("-02").And.StartWith(first[..3]);

        var entry = Entry(first);
        await outbox.AddAsync(entry);
        await outbox.FailedAsync(entry.Id, "No open business day");

        var restarted = new OfflineStore(store);
        await restarted.InitializeAsync();
        restarted.Outbox.Should().ContainSingle().Which.Should().Match<OfflineEntry>(e => e.Attempts == 1 && e.LastError == "No open business day");

        await restarted.SyncedAsync(entry.Id, new SyncedEntry(first, "014", Guid.NewGuid(), 100, false, null, DateTimeOffset.UtcNow));
        restarted.Outbox.Should().BeEmpty();
        restarted.Synced.Should().ContainSingle().Which.OrderNumber.Should().Be("014");
    }

    [Fact]
    public async Task An_unreadable_outbox_is_set_aside_not_wiped()
    {
        var store = new MemoryStore();
        store.Values["rp.offline.outbox"] = "[{\"broken";
        var outbox = new OfflineStore(store);
        await outbox.InitializeAsync();

        outbox.Outbox.Should().BeEmpty();
        store.Values.Keys.Should().Contain(k => k.StartsWith("rp.offline.outbox.unreadable."));
        store.Values.Single(kv => kv.Key.StartsWith("rp.offline.outbox.unreadable.")).Value.Should().Be("[{\"broken");
    }
}
