using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Menu;
using Restaurant.Contracts.Orders;
using Restaurant.Contracts.Sessions;
using Restaurant.Contracts.Tables;
using Restaurant.Contracts.Tenancy;

namespace Restaurant.Tests.Integration.Infrastructure;

/// <summary>A restaurant ready to sell: GST 16%, a small menu, two tables and (optionally) the day started.</summary>
public sealed record PosSetup(Caller Owner, Guid Burger, Guid Biryani, Guid BiryaniHalf, Guid BiryaniFull, Guid Drink,
    Guid ExtraCheese, Guid Grill, Guid Table1, Guid Table2, Guid CashMethod, Guid CardMethod);

public static class PosHelper
{
    public static async Task<PosSetup> SetUpRestaurantAsync(this ApiFactory factory, bool startDay = true, decimal openingCash = 5000m)
    {
        var owner = await factory.RegisterOwnerAsync();
        owner.UseBranch(owner.BranchId);

        await owner.PostAsync<TaxRateDto>("/api/tax-rates", new SaveTaxRateRequest("GST", 16m, false, OrderTypes.All, TaxPaymentScope.Any, null));
        var grill = await owner.PostAsync<KitchenStationDto>("/api/menu/stations", new SaveKitchenStationRequest("Grill", 1));
        var mains = await owner.PostAsync<MenuCategoryDto>("/api/menu/categories", new SaveMenuCategoryRequest("Mains", null, 1, null));
        var drinks = await owner.PostAsync<MenuCategoryDto>("/api/menu/categories", new SaveMenuCategoryRequest("Drinks", null, 2, null));
        var addOns = await owner.PostAsync<ModifierGroupDto>("/api/menu/modifier-groups", new SaveModifierGroupRequest("Add-ons", null, 0, 2, 1,
            [new(null, "Extra cheese", null, 100m, 1), new(null, "Jalapeño", null, 50m, 2)]));

        var burger = await owner.PostAsync<MenuItemDto>("/api/menu/items", new SaveMenuItemRequest(mains.Id, "Zinger Burger", null, null, "ZB", 550m, grill.Id, 1, true, [], [addOns.Id]));
        var biryani = await owner.PostAsync<MenuItemDto>("/api/menu/items", new SaveMenuItemRequest(mains.Id, "Chicken Biryani", null, null, null, 0m, null, 2, true,
            [new(null, "Half", null, 450m, null, 1), new(null, "Full", null, 850m, null, 2)], []));
        var drink = await owner.PostAsync<MenuItemDto>("/api/menu/items", new SaveMenuItemRequest(drinks.Id, "Mint Margarita", null, null, null, 300m, null, 1, true, [], []));

        var t1 = await owner.PostAsync<TableDto>("/api/tables", new SaveTableRequest(null, "1", 4));
        var t2 = await owner.PostAsync<TableDto>("/api/tables", new SaveTableRequest(null, "2", 2));
        var methods = await owner.GetAsync<List<PaymentMethodDto>>("/api/payment-methods");

        if (startDay)
        {
            await owner.PostAsync<BusinessSessionDto>("/api/sessions/start", new StartSessionRequest(openingCash, null));
        }

        return new PosSetup(owner, burger.Id, biryani.Id, biryani.Variants[0].Id, biryani.Variants[1].Id, drink.Id,
            addOns.Options[0].Id, grill.Id, t1.Id, t2.Id, methods.Single(m => m.Kind == PaymentMethodKind.Cash).Id, methods.Single(m => m.Kind == PaymentMethodKind.Card).Id);
    }

    /// <summary>2 × burger with extra cheese (2 × 650) + 1 × mint margarita (300) = 1600; GST 16% = 256; total 1856.</summary>
    public static CreateOrderRequest StandardOrder(this PosSetup pos, bool send = true, Guid? table = null, Guid? clientRequestId = null) =>
        new(clientRequestId ?? Guid.NewGuid(), table is null ? OrderType.Takeaway : OrderType.DineIn, table, null, null, null, null,
            [new(pos.Burger, null, 2, [pos.ExtraCheese], "No onions"), new(pos.Drink, null, 1, [], null)], null, send);

    public static Task<OrderDto> PayCashAsync(this Caller caller, Guid orderId, decimal amount, decimal? tendered, PosSetup pos) =>
        caller.PostAsync<OrderDto>($"/api/orders/{orderId}/payments", new TakePaymentRequest(Guid.NewGuid(), pos.CashMethod, amount, tendered, null));
}
