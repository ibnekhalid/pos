namespace Restaurant.Contracts.Auth;

/// <summary>
/// Every permission a tenant role can be granted (PLAN.md section 5). The API checks these with
/// [RequirePermission(...)]; the clients use them only to hide what the user can't do. Roles are data
/// (owners can create their own), so code never checks a role name.
/// </summary>
public static class Permissions
{
    public static class Tenant
    {
        public const string Manage = "tenant.manage";
        public const string ManageBranches = "branches.manage";
        public const string TransferOwnership = "ownership.transfer";
        public const string ManageSettings = "settings.manage";
        public const string ManageBranding = "branding.manage";
    }

    public static class Staff
    {
        public const string View = "staff.view";
        public const string Manage = "staff.manage";
        public const string ManageRoles = "roles.manage";
    }

    public static class Menu
    {
        public const string Manage = "menu.manage";
        public const string Availability = "menu.availability";
    }

    public static class Deals
    {
        public const string View = "deals.view";
        public const string Manage = "deals.manage";
        public const string ManualDiscount = "discounts.manual";
    }

    public static class Sessions
    {
        public const string Start = "sessions.start";
        public const string Close = "sessions.close";
        public const string Reopen = "sessions.reopen";
        public const string CashMovements = "cash.movements";
    }

    public static class Orders
    {
        public const string Create = "orders.create";
        public const string Edit = "orders.edit";
        public const string SendToKitchen = "orders.send_to_kitchen";
        public const string RequestCancel = "orders.cancel_request";
        public const string Void = "orders.void";
    }

    public static class Payments
    {
        public const string Take = "payments.take";
        public const string Refund = "payments.refund";
        public const string PrintReceipts = "receipts.print";
    }

    public static class Kitchen
    {
        public const string View = "kitchen.view";
        public const string Update = "kitchen.update";
    }

    public static class Inventory
    {
        public const string Receive = "inventory.receive";
        public const string Count = "inventory.count";
        public const string Wastage = "inventory.wastage";
        public const string Adjust = "inventory.adjust";
    }

    public static class Customers
    {
        public const string View = "customers.view";
        public const string Manage = "customers.manage";
    }

    public static class Reports
    {
        public const string Sales = "reports.sales";
        public const string Financial = "reports.financial";
        public const string Inventory = "reports.inventory";
        public const string Audit = "audit.view";
    }

    /// <summary>The full catalog, listed explicitly (no reflection) so trimming in the browser can't drop entries.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Tenant.Manage, Tenant.ManageBranches, Tenant.TransferOwnership, Tenant.ManageSettings, Tenant.ManageBranding,
        Staff.View, Staff.Manage, Staff.ManageRoles,
        Menu.Manage, Menu.Availability,
        Deals.View, Deals.Manage, Deals.ManualDiscount,
        Sessions.Start, Sessions.Close, Sessions.Reopen, Sessions.CashMovements,
        Orders.Create, Orders.Edit, Orders.SendToKitchen, Orders.RequestCancel, Orders.Void,
        Payments.Take, Payments.Refund, Payments.PrintReceipts,
        Kitchen.View, Kitchen.Update,
        Inventory.Receive, Inventory.Count, Inventory.Wastage, Inventory.Adjust,
        Customers.View, Customers.Manage,
        Reports.Sales, Reports.Financial, Reports.Inventory, Reports.Audit
    ];
}
