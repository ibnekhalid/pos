using Restaurant.Contracts.Auth;

namespace Restaurant.Client.UI;

/// <summary>
/// The main navigation (PLAN.md section 11). A section shows for anyone holding one of its
/// permissions (none listed = everyone). Sections not built yet open a "planned" page.
/// </summary>
public static class AppSections
{
    public record Section(string Slug, string TitleKey, string Icon, int Phase, string SummaryKey, string[] AnyOf, bool Built);

    public static IReadOnlyList<Section> All { get; } =
    [
        new("", "nav.home", "home-outline", 2, "nav.homeSummary", [], true),
        new("pos", "nav.pos", "fast-food-outline", 3, "nav.posSummary", [Permissions.Orders.Create], true),
        new("kitchen", "nav.kitchen", "flame-outline", 3, "nav.kitchenSummary", [Permissions.Kitchen.View], true),
        new("orders", "nav.orders", "receipt-outline", 3, "nav.ordersSummary", [Permissions.Orders.Create, Permissions.Orders.Edit, Permissions.Payments.Take], true),
        new("sessions", "nav.sessions", "time-outline", 3, "nav.sessionsSummary", [Permissions.Sessions.Start, Permissions.Sessions.Close, Permissions.Sessions.CashMovements], true),
        new("menu", "nav.menu", "restaurant-outline", 3, "nav.menuSummary", [Permissions.Menu.Manage, Permissions.Menu.Availability], true),
        new("deals", "nav.deals", "pricetags-outline", 4, "nav.dealsSummary", [Permissions.Deals.View, Permissions.Deals.Manage], true),
        new("inventory", "nav.inventory", "cube-outline", 4, "nav.inventorySummary",
            [Permissions.Inventory.Receive, Permissions.Inventory.Count, Permissions.Inventory.Wastage, Permissions.Inventory.Adjust, Permissions.Reports.Inventory], true),
        new("customers", "nav.customers", "people-outline", 4, "nav.customersSummary", [Permissions.Customers.View, Permissions.Customers.Manage], true),
        new("reports", "nav.reports", "bar-chart-outline", 5, "nav.reportsSummary",
            [Permissions.Reports.Sales, Permissions.Reports.Financial, Permissions.Reports.Inventory], true),
        new("staff", "nav.staff", "people-circle-outline", 2, "nav.staffSummary", [Permissions.Staff.View], true),
        new("settings", "nav.settings", "settings-outline", 2, "nav.settingsSummary", [], true)
    ];

    public static Section? Find(string slug) => All.FirstOrDefault(s => s.Slug == slug);
}
