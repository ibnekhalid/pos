using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Restaurant.Domain.Entities;

namespace Restaurant.Application.Common.Interfaces;

/// <summary>
/// The database as the use cases see it. Every tenant-owned set is filtered to the current tenant;
/// the few flows that must look across tenants (sign-in, invites, PIN sign-in, platform admin) call
/// IgnoreQueryFilters() explicitly and scope the query themselves.
/// </summary>
public interface IApplicationDbContext
{
    DbSet<AppUser> Users { get; }
    DbSet<Tenant> Tenants { get; }
    DbSet<Branch> Branches { get; }
    DbSet<Register> Registers { get; }
    DbSet<StaffMembership> Memberships { get; }
    DbSet<MembershipRole> MembershipRoles { get; }
    DbSet<MembershipBranch> MembershipBranches { get; }
    DbSet<TenantRole> TenantRoles { get; }
    DbSet<RolePermission> RolePermissions { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<RegisteredDevice> Devices { get; }
    DbSet<TaxRate> TaxRates { get; }
    DbSet<PaymentMethodConfig> PaymentMethods { get; }
    DbSet<BrandingSettings> BrandingSettings { get; }
    DbSet<ReceiptTemplate> ReceiptTemplates { get; }
    DbSet<FileAsset> FileAssets { get; }
    DbSet<AuditLog> AuditLogs { get; }

    DbSet<KitchenStation> KitchenStations { get; }
    DbSet<MenuCategory> MenuCategories { get; }
    DbSet<MenuItem> MenuItems { get; }
    DbSet<MenuItemVariant> MenuItemVariants { get; }
    DbSet<ModifierGroup> ModifierGroups { get; }
    DbSet<ModifierOption> ModifierOptions { get; }
    DbSet<MenuItemModifierGroup> MenuItemModifierGroups { get; }
    DbSet<BranchMenuItem> BranchMenuItems { get; }
    DbSet<DiningArea> DiningAreas { get; }
    DbSet<RestaurantTable> Tables { get; }
    DbSet<BusinessSession> BusinessSessions { get; }
    DbSet<RegisterSession> RegisterSessions { get; }
    DbSet<CashMovement> CashMovements { get; }
    DbSet<Order> Orders { get; }
    DbSet<OrderLine> OrderLines { get; }
    DbSet<OrderStatusHistory> OrderStatusHistory { get; }
    DbSet<KitchenTicket> KitchenTickets { get; }
    DbSet<Payment> Payments { get; }
    DbSet<Refund> Refunds { get; }
    DbSet<ReceiptPrint> ReceiptPrints { get; }

    DbSet<InventoryItem> InventoryItems { get; }
    DbSet<InventoryItemUnit> InventoryItemUnits { get; }
    DbSet<InventoryBalance> InventoryBalances { get; }
    DbSet<StockMovement> StockMovements { get; }
    DbSet<StockReceipt> StockReceipts { get; }
    DbSet<StockCount> StockCounts { get; }
    DbSet<RecipeLine> RecipeLines { get; }
    DbSet<Deal> Deals { get; }
    DbSet<DealItem> DealItems { get; }
    DbSet<DealBranch> DealBranches { get; }
    DbSet<OrderDealApplication> OrderDealApplications { get; }
    DbSet<Customer> Customers { get; }
    DbSet<Supplier> Suppliers { get; }
    DbSet<PurchaseOrder> PurchaseOrders { get; }
    DbSet<PurchaseOrderLine> PurchaseOrderLines { get; }
    DbSet<StockTransfer> StockTransfers { get; }
    DbSet<StockTransferLine> StockTransferLines { get; }
    DbSet<Reservation> Reservations { get; }

    DatabaseFacade Database { get; }
    ChangeTracker ChangeTracker { get; }

    /// <summary>The next order number for a branch and business date (atomic, safe under concurrency).</summary>
    Task<int> NextOrderSequenceAsync(Guid branchId, DateOnly businessDate, CancellationToken cancellationToken);

    /// <summary>A unique index rejected the write (duplicate ClientRequestId, second open session...).</summary>
    bool IsUniqueViolation(Exception exception);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
