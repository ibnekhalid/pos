using System.Net;
using FluentAssertions;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Inventory;
using Restaurant.Contracts.Orders;
using Restaurant.Contracts.Tenancy;
using Restaurant.Tests.Integration.Infrastructure;

namespace Restaurant.Tests.Integration;

/// <summary>Stock (PLAN.md section 9): T9 deduction on send-to-kitchen, T10 reversal on void, receipts, wastage and counts.</summary>
public class InventoryTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private sealed record Stock(PosSetup Pos, Guid Chicken, Guid Cheese);

    /// <summary>Chicken 2 kg at Rs 1,000/kg and cheese 1 kg; burger = 150 g chicken, extra cheese = 20 g cheese.</summary>
    private async Task<Stock> SetUpAsync(InsufficientStockBehavior policy = InsufficientStockBehavior.Allow)
    {
        var pos = await factory.SetUpRestaurantAsync();
        var owner = pos.Owner;
        if (policy != InsufficientStockBehavior.Allow)
        {
            var branch = await owner.GetAsync<BranchDto>($"/api/branches/{owner.BranchId}");
            await owner.PutAsync<BranchDto>($"/api/branches/{owner.BranchId}/settings", branch.Settings with { InsufficientStock = policy });
        }

        var chicken = await owner.PostAsync<InventoryItemDto>("/api/inventory/items", new SaveInventoryItemRequest("Chicken", null, "g", "Meat", 500, true, []));
        var cheese = await owner.PostAsync<InventoryItemDto>("/api/inventory/items", new SaveInventoryItemRequest("Cheese slice", null, "g", "Dairy", 0, true, []));
        await owner.PostAsync<StockReceiptDto>("/api/inventory/receipts", new CreateStockReceiptRequest(Guid.NewGuid(), "Kareem Poultry", "INV-1", null,
            [new(chicken.Id, 2, "kg", 1000), new(cheese.Id, 1000, "g", 2)]));
        await owner.PutAsync<RecipeDto>("/api/inventory/recipes", new RecipeDto(pos.Burger, null, null, [new(chicken.Id, 150, "g")]));
        await owner.PutAsync<RecipeDto>("/api/inventory/recipes", new RecipeDto(null, null, pos.ExtraCheese, [new(cheese.Id, 20, "g")]));
        return new Stock(pos, chicken.Id, cheese.Id);
    }

    private static async Task<decimal> OnHandAsync(Caller caller, Guid itemId) =>
        (await caller.GetAsync<List<StockLevelDto>>("/api/inventory/levels")).Single(l => l.ItemId == itemId).OnHand;

    [Fact]
    public async Task T9_sending_to_the_kitchen_deducts_the_recipe_once_and_T10_voids_put_it_back()
    {
        var s = await SetUpAsync();
        var owner = s.Pos.Owner;

        // Held orders use nothing yet.
        var request = s.Pos.StandardOrder(send: false);
        var order = await owner.PostAsync<OrderDto>("/api/orders", request);
        (await OnHandAsync(owner, s.Chicken)).Should().Be(2000);

        // 2 burgers with extra cheese: 300 g chicken, 40 g cheese.
        order = await owner.PostAsync<OrderDto>($"/api/orders/{order.Id}/send", new { });
        (await OnHandAsync(owner, s.Chicken)).Should().Be(1700);
        (await OnHandAsync(owner, s.Cheese)).Should().Be(960);

        // Retrying the create or sending again never books it twice.
        await owner.PostAsync<OrderDto>("/api/orders", request);
        (await owner.RawPostAsync($"/api/orders/{order.Id}/send", new { })).ShouldBe(HttpStatusCode.Conflict);
        (await OnHandAsync(owner, s.Chicken)).Should().Be(1700);

        // T10: voiding the burgers puts their chicken and cheese back, exactly once.
        var burgerLine = order.Lines.Single(l => l.MenuItemId == s.Pos.Burger);
        await owner.PostAsync<OrderDto>($"/api/orders/{order.Id}/lines/{burgerLine.Id}/void", new ReasonRequest("Customer changed mind"));
        (await OnHandAsync(owner, s.Chicken)).Should().Be(2000);
        (await OnHandAsync(owner, s.Cheese)).Should().Be(1000);
        (await owner.RawPostAsync($"/api/orders/{order.Id}/lines/{burgerLine.Id}/void", new ReasonRequest("again"))).ShouldBe(HttpStatusCode.NotFound);

        var ledger = await owner.GetAsync<List<StockMovementDto>>($"/api/inventory/movements?itemId={s.Chicken}");
        ledger.Select(m => (m.Type, m.Quantity)).Should().BeEquivalentTo(new[]
        {
            (StockMovementType.Receipt, 2000m), (StockMovementType.SaleConsumption, -300m), (StockMovementType.SaleReversal, 300m)
        });
        ledger.Single(m => m.Type == StockMovementType.SaleConsumption).Reference.Should().Be($"Order {order.OrderNumber}");
    }

    [Fact]
    public async Task Voiding_a_whole_order_reverses_everything_it_used()
    {
        var s = await SetUpAsync();
        var owner = s.Pos.Owner;
        var order = await owner.PostAsync<OrderDto>("/api/orders", s.Pos.StandardOrder());
        (await OnHandAsync(owner, s.Chicken)).Should().Be(1700);

        await owner.PostAsync<OrderDto>($"/api/orders/{order.Id}/void", new ReasonRequest("Walked out"));
        (await OnHandAsync(owner, s.Chicken)).Should().Be(2000);
        (await OnHandAsync(owner, s.Cheese)).Should().Be(1000);
    }

    [Fact]
    public async Task Sizes_use_their_own_recipe_and_the_branch_policy_can_warn_or_block()
    {
        var s = await SetUpAsync(InsufficientStockBehavior.Warn);
        var owner = s.Pos.Owner;
        await owner.PutAsync<RecipeDto>("/api/inventory/recipes", new RecipeDto(s.Pos.Biryani, s.Pos.BiryaniFull, null, [new(s.Chicken, 1.5m, "kg")]));

        var warned = await owner.PostAsync<OrderDto>("/api/orders", new CreateOrderRequest(Guid.NewGuid(), OrderType.Takeaway, null, null, null, null, null,
            [new(s.Pos.Biryani, s.Pos.BiryaniFull, 2, [], null)], null, true));
        warned.StockWarnings.Should().ContainSingle().Which.Should().Contain("Not enough Chicken").And.Contain("3 kg").And.Contain("2 kg");
        (await OnHandAsync(owner, s.Chicken)).Should().Be(-1000);

        // The half size has no recipe of its own and the item has none: nothing is deducted.
        await owner.PostAsync<OrderDto>("/api/orders", new CreateOrderRequest(Guid.NewGuid(), OrderType.Takeaway, null, null, null, null, null,
            [new(s.Pos.Biryani, s.Pos.BiryaniHalf, 1, [], null)], null, true));
        (await OnHandAsync(owner, s.Chicken)).Should().Be(-1000);

        var branch = await owner.GetAsync<BranchDto>($"/api/branches/{owner.BranchId}");
        await owner.PutAsync<BranchDto>($"/api/branches/{owner.BranchId}/settings", branch.Settings with { InsufficientStock = InsufficientStockBehavior.Block });
        var blocked = await owner.RawPostAsync("/api/orders", s.Pos.StandardOrder());
        blocked.ShouldBe(HttpStatusCode.Conflict);
        (await blocked.Content.ReadAsStringAsync()).Should().Contain("Not enough Chicken");
        (await owner.GetAsync<List<OrderSummaryDto>>("/api/orders")).Should().HaveCount(2); // the blocked order wasn't saved
    }

    [Fact]
    public async Task Receipts_keep_a_weighted_average_cost_and_counts_post_the_variance()
    {
        var s = await SetUpAsync();
        var owner = s.Pos.Owner;

        // 2 kg at 1.00/g, then 2 kg at 1.40/g → 1.20/g.
        await owner.PostAsync<StockReceiptDto>("/api/inventory/receipts", new CreateStockReceiptRequest(Guid.NewGuid(), null, null, null, [new(s.Chicken, 2000, "g", 1.4m)]));
        var level = (await owner.GetAsync<List<StockLevelDto>>("/api/inventory/levels")).Single(l => l.ItemId == s.Chicken);
        level.OnHand.Should().Be(4000);
        level.AverageCost.Should().Be(1.2m);
        level.Value.Should().Be(4800);

        var count = await owner.PostAsync<StockCountDto>("/api/inventory/counts", new CreateStockCountRequest(Guid.NewGuid(), [new(s.Chicken, 3.5m, "kg")], "Month end"));
        count.Lines.Single().Variance.Should().Be(-500);
        count.VarianceValue.Should().Be(-600);
        (await OnHandAsync(owner, s.Chicken)).Should().Be(3500);
        (await owner.GetAsync<List<StockCountDto>>("/api/inventory/counts")).Should().ContainSingle(c => c.Id == count.Id);
    }

    [Fact]
    public async Task Wastage_needs_permission_and_a_reason_and_adjustments_can_be_approved_by_a_manager()
    {
        var s = await SetUpAsync();
        var owner = s.Pos.Owner;
        var (cashier, _, _) = await factory.AddStaffAsync(owner, ["Cashier"]);
        var (manager, managerMember, _) = await factory.AddStaffAsync(owner, ["Manager"]);
        await manager.RawPutAsync("/api/me/pin", new Restaurant.Contracts.Auth.SetPinRequest(TestApi.Password, "2580"));
        cashier.UseBranch(owner.BranchId);

        (await owner.RawPostAsync("/api/inventory/adjustments", new StockAdjustmentRequest(Guid.NewGuid(), s.Chicken, StockMovementType.Wastage, 100, "g", " ")))
            .ShouldBe(HttpStatusCode.BadRequest);
        var wasted = await owner.PostAsync<StockMovementDto>("/api/inventory/adjustments",
            new StockAdjustmentRequest(Guid.NewGuid(), s.Chicken, StockMovementType.Wastage, 0.2m, "kg", "Fridge failure"));
        wasted.Quantity.Should().Be(-200);
        (await OnHandAsync(owner, s.Chicken)).Should().Be(1800);

        (await cashier.RawPostAsync("/api/inventory/adjustments", new StockAdjustmentRequest(Guid.NewGuid(), s.Chicken, StockMovementType.Wastage, 50, "g", "Dropped")))
            .ShouldBe(HttpStatusCode.Forbidden);
        (await cashier.RawPostAsync("/api/inventory/adjustments", new StockAdjustmentRequest(Guid.NewGuid(), s.Chicken, StockMovementType.Adjustment, 50, "g", "Found")))
            .ShouldBe(HttpStatusCode.Forbidden);

        // With the manager's PIN the cashier can correct the books.
        var adjust = new StockAdjustmentRequest(Guid.NewGuid(), s.Chicken, StockMovementType.Adjustment, -100, "g", "Miscounted delivery",
            new Approval(managerMember.MembershipId, "2580"));
        (await cashier.PostAsync<StockMovementDto>("/api/inventory/adjustments", adjust)).Quantity.Should().Be(-100);
        await cashier.PostAsync<StockMovementDto>("/api/inventory/adjustments", adjust); // retry: booked once
        (await OnHandAsync(owner, s.Chicken)).Should().Be(1700);

        (await owner.RawPostAsync("/api/inventory/adjustments", new StockAdjustmentRequest(Guid.NewGuid(), s.Chicken, StockMovementType.Wastage, 1, "ml", "Spilt")))
            .ShouldBe(HttpStatusCode.BadRequest); // chicken is weighed, not measured in ml
    }

    [Fact]
    public async Task Low_stock_lists_items_at_their_reorder_level_or_below_zero()
    {
        var pos = await factory.SetUpRestaurantAsync();
        var owner = pos.Owner;
        var oil = await owner.PostAsync<InventoryItemDto>("/api/inventory/items", new SaveInventoryItemRequest("Oil", null, "ml", null, 2000, true, []));
        var salt = await owner.PostAsync<InventoryItemDto>("/api/inventory/items", new SaveInventoryItemRequest("Salt", null, "g", null, 0, true, []));
        var rice = await owner.PostAsync<InventoryItemDto>("/api/inventory/items", new SaveInventoryItemRequest("Rice", null, "g", null, 1000, true, []));
        await owner.PostAsync<StockReceiptDto>("/api/inventory/receipts", new CreateStockReceiptRequest(Guid.NewGuid(), null, null, null,
            [new(oil.Id, 1500, "ml", 1), new(rice.Id, 5, "kg", 200)]));
        await owner.PostAsync<StockMovementDto>("/api/inventory/adjustments", new StockAdjustmentRequest(Guid.NewGuid(), salt.Id, StockMovementType.Adjustment, -50, "g", "Spilt"));

        var low = await owner.GetAsync<List<LowStockDto>>("/api/inventory/low-stock");
        low.Select(l => l.Name).Should().BeEquivalentTo(["Oil", "Salt"]); // rice is above its level; salt has none but is negative
        low.Single(l => l.Name == "Oil").Shortfall.Should().Be(500);

        var (waiter, _, _) = await factory.AddStaffAsync(owner, ["Waiter"]);
        waiter.UseBranch(owner.BranchId);
        (await waiter.RawGetAsync("/api/inventory/low-stock")).ShouldBe(System.Net.HttpStatusCode.Forbidden);
    }
}
