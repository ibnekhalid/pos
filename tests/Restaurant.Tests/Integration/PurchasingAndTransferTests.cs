using System.Net;
using FluentAssertions;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Inventory;
using Restaurant.Contracts.Reports;
using Restaurant.Contracts.Sessions;
using Restaurant.Contracts.Tenancy;
using Restaurant.Tests.Integration.Infrastructure;

namespace Restaurant.Tests.Integration;

/// <summary>Suppliers, purchase orders received in parts, and stock sent between branches.</summary>
public class PurchasingAndTransferTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static async Task<decimal> OnHandAsync(Caller caller, Guid itemId) =>
        (await caller.GetAsync<List<StockLevelDto>>("/api/inventory/levels")).Single(l => l.ItemId == itemId).OnHand;

    [Fact]
    public async Task A_purchase_order_is_received_in_parts_and_closes_itself()
    {
        var pos = await factory.SetUpRestaurantAsync(startDay: false);
        var owner = pos.Owner;
        var chicken = await owner.PostAsync<InventoryItemDto>("/api/inventory/items", new SaveInventoryItemRequest("Chicken", null, "g", null, 0, true, []));
        var cheese = await owner.PostAsync<InventoryItemDto>("/api/inventory/items", new SaveInventoryItemRequest("Cheese", null, "g", null, 0, true, []));
        var supplier = await owner.PostAsync<SupplierDto>("/api/inventory/suppliers", new SaveSupplierRequest("Kareem Poultry", "Kareem", "03001234567", null, null));
        (await owner.RawPostAsync("/api/inventory/suppliers", new SaveSupplierRequest("Kareem Poultry", null, null, null, null))).ShouldBe(HttpStatusCode.Conflict);

        var po = await owner.PostAsync<PurchaseOrderDto>("/api/inventory/purchase-orders", new SavePurchaseOrderRequest(supplier.Id, null, null,
            [new(chicken.Id, 4, "kg", 900)]));
        po.Number.Should().Be("PO-001");
        po.Status.Should().Be(PurchaseOrderStatus.Draft);

        // A draft can be changed; it can't be received against until it's placed.
        po = await owner.PutAsync<PurchaseOrderDto>($"/api/inventory/purchase-orders/{po.Id}", new SavePurchaseOrderRequest(supplier.Id, null, "Friday please",
            [new(chicken.Id, 5, "kg", 900), new(cheese.Id, 1000, "g", 2)]));
        po.Total.Should().Be(5 * 900 + 1000 * 2);
        po.Lines.Single(l => l.ItemId == chicken.Id).QuantityBase.Should().Be(5000);
        (await owner.RawPostAsync("/api/inventory/receipts", new CreateStockReceiptRequest(Guid.NewGuid(), null, null, null, [new(chicken.Id, 1, "kg", 900)], null, po.Id)))
            .ShouldBe(HttpStatusCode.Conflict);

        po = await owner.PostAsync<PurchaseOrderDto>($"/api/inventory/purchase-orders/{po.Id}/place", new { });
        po.Status.Should().Be(PurchaseOrderStatus.Ordered);
        (await owner.RawPutAsync($"/api/inventory/purchase-orders/{po.Id}", new SavePurchaseOrderRequest(supplier.Id, null, null, [new(chicken.Id, 1, "kg", 1)])))
            .ShouldBe(HttpStatusCode.Conflict);

        // First delivery: 3 kg of chicken. The receipt takes the supplier from the order.
        var first = await owner.PostAsync<StockReceiptDto>("/api/inventory/receipts", new CreateStockReceiptRequest(Guid.NewGuid(), null, "INV-7", null,
            [new(chicken.Id, 3000, "g", 0.9m)], null, po.Id));
        first.Supplier.Should().Be("Kareem Poultry");
        first.PurchaseOrderId.Should().Be(po.Id);
        po = await owner.GetAsync<PurchaseOrderDto>($"/api/inventory/purchase-orders/{po.Id}");
        po.Status.Should().Be(PurchaseOrderStatus.PartiallyReceived);
        po.Lines.Single(l => l.ItemId == chicken.Id).ReceivedBase.Should().Be(3000);

        // The rest arrives: the order is received in full and closed for more.
        await owner.PostAsync<StockReceiptDto>("/api/inventory/receipts", new CreateStockReceiptRequest(Guid.NewGuid(), null, null, null,
            [new(chicken.Id, 2, "kg", 900), new(cheese.Id, 1000, "g", 2)], null, po.Id));
        (await owner.GetAsync<PurchaseOrderDto>($"/api/inventory/purchase-orders/{po.Id}")).Status.Should().Be(PurchaseOrderStatus.Received);
        (await owner.RawPostAsync("/api/inventory/receipts", new CreateStockReceiptRequest(Guid.NewGuid(), null, null, null, [new(cheese.Id, 1, "g", 2)], null, po.Id)))
            .ShouldBe(HttpStatusCode.Conflict);
        (await OnHandAsync(owner, chicken.Id)).Should().Be(5000);

        // Another restaurant can't see or use it.
        var other = await factory.SetUpRestaurantAsync(startDay: false);
        (await other.Owner.RawGetAsync($"/api/inventory/purchase-orders/{po.Id}")).ShouldBe(HttpStatusCode.NotFound);
        (await other.Owner.RawPostAsync($"/api/inventory/purchase-orders/{po.Id}/cancel", new { })).ShouldBe(HttpStatusCode.NotFound);
        (await other.Owner.GetAsync<List<SupplierDto>>("/api/inventory/suppliers")).Should().BeEmpty();

        // A waiter can't buy stock.
        var (waiter, _, _) = await factory.AddStaffAsync(owner, ["Waiter"]);
        waiter.UseBranch(owner.BranchId);
        (await waiter.RawGetAsync("/api/inventory/purchase-orders")).ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Stock_sent_to_another_branch_leaves_now_and_arrives_when_received()
    {
        var pos = await factory.SetUpRestaurantAsync();
        var owner = pos.Owner;
        var main = owner.BranchId;
        var gulberg = (await owner.PostAsync<BranchDto>("/api/branches", new SaveBranchRequest("Gulberg", null, "Lahore", null, "G"))).Id;
        var chicken = await owner.PostAsync<InventoryItemDto>("/api/inventory/items", new SaveInventoryItemRequest("Chicken", null, "g", null, 0, true, []));
        await owner.PostAsync<StockReceiptDto>("/api/inventory/receipts", new CreateStockReceiptRequest(Guid.NewGuid(), null, null, null, [new(chicken.Id, 5, "kg", 1000)]));

        var request = new CreateStockTransferRequest(Guid.NewGuid(), gulberg, "For the weekend", [new(chicken.Id, 2, "kg")]);
        var transfer = await owner.PostAsync<StockTransferDto>("/api/inventory/transfers", request);
        (await owner.PostAsync<StockTransferDto>("/api/inventory/transfers", request)).Id.Should().Be(transfer.Id); // a retry sends nothing twice
        transfer.Number.Should().Be("TR-001");
        transfer.Status.Should().Be(StockTransferStatus.InTransit);
        transfer.TotalCost.Should().Be(2000);
        transfer.ToBranchName.Should().Be("Gulberg");
        (await OnHandAsync(owner, chicken.Id)).Should().Be(3000);

        // Only the destination receives it; the sender can't, and can't cancel once it has arrived.
        (await owner.RawPostAsync($"/api/inventory/transfers/{transfer.Id}/receive", new { })).ShouldBe(HttpStatusCode.Conflict);
        owner.UseBranch(gulberg);
        (await OnHandAsync(owner, chicken.Id)).Should().Be(0);
        var received = await owner.PostAsync<StockTransferDto>($"/api/inventory/transfers/{transfer.Id}/receive", new { });
        received.Status.Should().Be(StockTransferStatus.Received);
        var level = (await owner.GetAsync<List<StockLevelDto>>("/api/inventory/levels")).Single(l => l.ItemId == chicken.Id);
        level.OnHand.Should().Be(2000);
        level.AverageCost.Should().Be(1);
        (await owner.RawPostAsync($"/api/inventory/transfers/{transfer.Id}/receive", new { })).ShouldBe(HttpStatusCode.Conflict);
        owner.UseBranch(main);
        (await owner.RawPostAsync($"/api/inventory/transfers/{transfer.Id}/cancel", new { })).ShouldBe(HttpStatusCode.Conflict);

        // A second one is called back while in transit: the stock comes home.
        var back = await owner.PostAsync<StockTransferDto>("/api/inventory/transfers", new CreateStockTransferRequest(Guid.NewGuid(), gulberg, null, [new(chicken.Id, 500, "g")]));
        (await OnHandAsync(owner, chicken.Id)).Should().Be(2500);
        (await owner.PostAsync<StockTransferDto>($"/api/inventory/transfers/{back.Id}/cancel", new { })).Status.Should().Be(StockTransferStatus.Cancelled);
        (await OnHandAsync(owner, chicken.Id)).Should().Be(3000);
        (await owner.GetAsync<List<StockTransferDto>>("/api/inventory/transfers")).Should().HaveCount(2);

        (await owner.RawPostAsync("/api/inventory/transfers", new CreateStockTransferRequest(Guid.NewGuid(), main, null, [new(chicken.Id, 1, "g")])))
            .ShouldBe(HttpStatusCode.BadRequest);

        // The stock report counts it as transferred.
        var session = await owner.GetAsync<BusinessSessionDto>("/api/sessions/current");
        var report = await owner.GetAsync<InventoryReportDto>($"/api/reports/inventory?from={session.BusinessDate:yyyy-MM-dd}&to={session.BusinessDate:yyyy-MM-dd}");
        report.Activity.Single(a => a.ItemId == chicken.Id).Transferred.Should().Be(-2000);
    }
}
