using Restaurant.Contracts.Auth;
using Restaurant.Domain.Constants;
using Restaurant.Domain.Entities;

namespace Restaurant.Application.Features.Staff;

/// <summary>
/// The roles every new tenant starts with (PLAN.md section 5). They are ordinary rows afterwards:
/// owners can edit all but Owner, and create their own.
/// </summary>
public static class SystemRoles
{
    private static readonly (string Key, string Name, string Description, bool FullAccess, string[] Permissions)[] Definitions =
    [
        (SystemRoleKeys.Owner, "Owner", "Full access to the restaurant, including staff, settings and all reports.", true, []),
        (SystemRoleKeys.Manager, "Manager", "Runs day-to-day operations: business day, orders, kitchen, stock, approvals.", false,
        [
            Permissions.Staff.View,
            Permissions.Menu.Availability,
            Permissions.Deals.View, Permissions.Deals.ManualDiscount,
            Permissions.Sessions.Start, Permissions.Sessions.Close, Permissions.Sessions.CashMovements,
            Permissions.Orders.Create, Permissions.Orders.Edit, Permissions.Orders.SendToKitchen, Permissions.Orders.RequestCancel, Permissions.Orders.Void,
            Permissions.Payments.Take, Permissions.Payments.Refund, Permissions.Payments.PrintReceipts,
            Permissions.Kitchen.View, Permissions.Kitchen.Update,
            Permissions.Inventory.Receive, Permissions.Inventory.Count, Permissions.Inventory.Wastage, Permissions.Inventory.Adjust,
            Permissions.Customers.View, Permissions.Customers.Manage,
            Permissions.Reports.Sales, Permissions.Reports.Inventory
        ]),
        (SystemRoleKeys.Cashier, "Cashier", "Takes orders and payments, prints receipts, runs the register.", false,
        [
            Permissions.Sessions.Start, Permissions.Sessions.Close, Permissions.Sessions.CashMovements,
            Permissions.Orders.Create, Permissions.Orders.Edit, Permissions.Orders.SendToKitchen, Permissions.Orders.RequestCancel,
            Permissions.Payments.Take, Permissions.Payments.PrintReceipts,
            Permissions.Customers.View
        ]),
        (SystemRoleKeys.Waiter, "Waiter", "Takes dine-in orders at tables and sends them to the kitchen.", false,
        [
            Permissions.Orders.Create, Permissions.Orders.Edit, Permissions.Orders.SendToKitchen, Permissions.Orders.RequestCancel,
            Permissions.Kitchen.View,
            Permissions.Customers.View
        ]),
        (SystemRoleKeys.Kitchen, "Kitchen staff", "Sees kitchen tickets and marks items as preparing and ready.", false,
        [
            Permissions.Kitchen.View, Permissions.Kitchen.Update
        ]),
        (SystemRoleKeys.Inventory, "Inventory staff", "Receives stock, records wastage and counts.", false,
        [
            Permissions.Inventory.Receive, Permissions.Inventory.Count, Permissions.Inventory.Wastage,
            Permissions.Reports.Inventory
        ])
    ];

    public static List<TenantRole> CreateFor(Guid tenantId) => Definitions
        .Select(d =>
        {
            var role = new TenantRole
            {
                TenantId = tenantId,
                Name = d.Name,
                Description = d.Description,
                IsSystem = true,
                SystemKey = d.Key,
                FullAccess = d.FullAccess
            };
            role.Permissions = d.Permissions
                .Select(p => new RolePermission { TenantId = tenantId, RoleId = role.Id, Permission = p })
                .ToList();
            return role;
        })
        .ToList();
}
