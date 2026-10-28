using System.Linq.Expressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Restaurant.Application.Common.Interfaces;
using Restaurant.Domain.Common;
using Restaurant.Domain.Constants;
using Restaurant.Domain.Entities;

namespace Restaurant.Infrastructure.Persistence;

/// <summary>
/// The single database context, including the ASP.NET Identity tables. Every ITenantOwned entity
/// gets a global query filter on the current tenant (PLAN.md section 4), and
/// TenantSaveChangesInterceptor refuses writes to another tenant's rows.
/// </summary>
public class RestaurantDbContext(DbContextOptions<RestaurantDbContext> options, ITenantContext tenant)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options), IApplicationDbContext
{
    public static readonly Guid PlatformAdminRoleId = new("0192f0a0-0000-7000-8000-0000000000a1");

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<Register> Registers => Set<Register>();
    public DbSet<StaffMembership> Memberships => Set<StaffMembership>();
    public DbSet<MembershipRole> MembershipRoles => Set<MembershipRole>();
    public DbSet<MembershipBranch> MembershipBranches => Set<MembershipBranch>();
    public DbSet<TenantRole> TenantRoles => Set<TenantRole>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<RegisteredDevice> Devices => Set<RegisteredDevice>();
    public DbSet<TaxRate> TaxRates => Set<TaxRate>();
    public DbSet<PaymentMethodConfig> PaymentMethods => Set<PaymentMethodConfig>();
    public DbSet<BrandingSettings> BrandingSettings => Set<BrandingSettings>();
    public DbSet<ReceiptTemplate> ReceiptTemplates => Set<ReceiptTemplate>();
    public DbSet<FileAsset> FileAssets => Set<FileAsset>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<KitchenStation> KitchenStations => Set<KitchenStation>();
    public DbSet<MenuCategory> MenuCategories => Set<MenuCategory>();
    public DbSet<MenuItem> MenuItems => Set<MenuItem>();
    public DbSet<MenuItemVariant> MenuItemVariants => Set<MenuItemVariant>();
    public DbSet<ModifierGroup> ModifierGroups => Set<ModifierGroup>();
    public DbSet<ModifierOption> ModifierOptions => Set<ModifierOption>();
    public DbSet<MenuItemModifierGroup> MenuItemModifierGroups => Set<MenuItemModifierGroup>();
    public DbSet<BranchMenuItem> BranchMenuItems => Set<BranchMenuItem>();
    public DbSet<DiningArea> DiningAreas => Set<DiningArea>();
    public DbSet<RestaurantTable> Tables => Set<RestaurantTable>();
    public DbSet<BusinessSession> BusinessSessions => Set<BusinessSession>();
    public DbSet<RegisterSession> RegisterSessions => Set<RegisterSession>();
    public DbSet<CashMovement> CashMovements => Set<CashMovement>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<OrderStatusHistory> OrderStatusHistory => Set<OrderStatusHistory>();
    public DbSet<KitchenTicket> KitchenTickets => Set<KitchenTicket>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<ReceiptPrint> ReceiptPrints => Set<ReceiptPrint>();
    public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();
    public DbSet<InventoryItemUnit> InventoryItemUnits => Set<InventoryItemUnit>();
    public DbSet<InventoryBalance> InventoryBalances => Set<InventoryBalance>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<StockReceipt> StockReceipts => Set<StockReceipt>();
    public DbSet<StockCount> StockCounts => Set<StockCount>();
    public DbSet<RecipeLine> RecipeLines => Set<RecipeLine>();
    public DbSet<Deal> Deals => Set<Deal>();
    public DbSet<DealItem> DealItems => Set<DealItem>();
    public DbSet<DealBranch> DealBranches => Set<DealBranch>();
    public DbSet<OrderDealApplication> OrderDealApplications => Set<OrderDealApplication>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<PurchaseOrderLine> PurchaseOrderLines => Set<PurchaseOrderLine>();
    public DbSet<StockTransfer> StockTransfers => Set<StockTransfer>();
    public DbSet<StockTransferLine> StockTransferLines => Set<StockTransferLine>();
    public DbSet<Reservation> Reservations => Set<Reservation>();

    /// <summary>
    /// Read by the query filters on every query (EF parameterises it per query). Guid.Empty when no
    /// tenant is selected, so unscoped requests see no tenant data at all.
    /// </summary>
    public Guid CurrentTenantId => tenant.TenantId ?? Guid.Empty;

    public async Task<int> NextOrderSequenceAsync(Guid branchId, DateOnly businessDate, CancellationToken cancellationToken)
    {
        // One atomic upsert: concurrent orders at the same branch can never get the same number.
        var connection = Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await Database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = """
            INSERT INTO "OrderSequences" ("BranchId", "BusinessDate", "LastNumber") VALUES (@branch, @date, 1)
            ON CONFLICT ("BranchId", "BusinessDate") DO UPDATE SET "LastNumber" = "OrderSequences"."LastNumber" + 1
            RETURNING "LastNumber"
            """;
        command.Parameters.Add(new NpgsqlParameter("branch", branchId));
        command.Parameters.Add(new NpgsqlParameter("date", businessDate));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public bool IsUniqueViolation(Exception exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Money everywhere is numeric(18,2); percentages override this to (6,3).
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(RestaurantDbContext).Assembly);

        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            if (typeof(ITenantOwned).IsAssignableFrom(entityType.ClrType) && entityType.BaseType is null)
            {
                builder.Entity(entityType.ClrType).HasQueryFilter(TenantFilter(entityType.ClrType));
            }
        }

        builder.Entity<AuditLog>().HasQueryFilter(a => a.TenantId == CurrentTenantId);
        builder.Entity<ReceiptTemplate>().HasQueryFilter(t => t.TenantId == null || t.TenantId == CurrentTenantId);

        builder.Entity<IdentityRole<Guid>>().HasData(new IdentityRole<Guid>
        {
            Id = PlatformAdminRoleId,
            Name = SystemRoleKeys.PlatformAdminRole,
            NormalizedName = SystemRoleKeys.PlatformAdminRole.ToUpperInvariant(),
            ConcurrencyStamp = "0192f0a0-platform-admin"
        });
    }

    private LambdaExpression TenantFilter(Type clrType)
    {
        // e => e.TenantId == this.CurrentTenantId
        var entity = Expression.Parameter(clrType, "e");
        var body = Expression.Equal(
            Expression.Property(entity, nameof(ITenantOwned.TenantId)),
            Expression.Property(Expression.Constant(this), nameof(CurrentTenantId)));
        return Expression.Lambda(body, entity);
    }
}
