using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Restaurant.Domain.Entities;

namespace Restaurant.Infrastructure.Persistence;

internal class InventoryItemConfiguration : IEntityTypeConfiguration<InventoryItem>
{
    public void Configure(EntityTypeBuilder<InventoryItem> b)
    {
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.NameUr).HasMaxLength(100);
        b.Property(x => x.BaseUnit).HasMaxLength(10).IsRequired();
        b.Property(x => x.Category).HasMaxLength(60);
        b.Property(x => x.ReorderLevel).HasPrecision(18, 3);
        b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
        b.HasMany(x => x.Units).WithOne().HasForeignKey(x => x.InventoryItemId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal class InventoryItemUnitConfiguration : IEntityTypeConfiguration<InventoryItemUnit>
{
    public void Configure(EntityTypeBuilder<InventoryItemUnit> b)
    {
        b.HasKey(x => new { x.InventoryItemId, x.Name });
        b.Property(x => x.Name).HasMaxLength(30);
        b.Property(x => x.FactorToBase).HasPrecision(18, 4);
    }
}

internal class InventoryBalanceConfiguration : IEntityTypeConfiguration<InventoryBalance>
{
    public void Configure(EntityTypeBuilder<InventoryBalance> b)
    {
        b.Property(x => x.AverageCost).HasPrecision(18, 4);
        b.Property(x => x.Version).IsRowVersion();
        b.HasIndex(x => new { x.BranchId, x.InventoryItemId }).IsUnique();
        b.HasOne<InventoryItem>().WithMany().HasForeignKey(x => x.InventoryItemId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> b)
    {
        b.Property(x => x.Quantity).HasPrecision(18, 3);
        b.Property(x => x.UnitCost).HasPrecision(18, 4);
        b.Property(x => x.SourceType).HasMaxLength(30);
        b.Property(x => x.IdempotencyKey).HasMaxLength(120).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(300);
        b.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique();
        b.HasIndex(x => new { x.BranchId, x.InventoryItemId });
        b.HasIndex(x => x.SourceId);
        b.HasOne<InventoryItem>().WithMany().HasForeignKey(x => x.InventoryItemId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal class StockReceiptConfiguration : IEntityTypeConfiguration<StockReceipt>
{
    public void Configure(EntityTypeBuilder<StockReceipt> b)
    {
        b.Property(x => x.Supplier).HasMaxLength(100);
        b.Property(x => x.InvoiceNumber).HasMaxLength(50);
        b.HasOne<Supplier>().WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PurchaseOrder>().WithMany().HasForeignKey(x => x.PurchaseOrderId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.Notes).HasMaxLength(300);
        b.HasIndex(x => new { x.TenantId, x.ClientRequestId }).IsUnique();
        b.HasIndex(x => x.BranchId);
    }
}

internal class StockCountConfiguration : IEntityTypeConfiguration<StockCount>
{
    public void Configure(EntityTypeBuilder<StockCount> b)
    {
        b.Property(x => x.Notes).HasMaxLength(300);
        b.Property(x => x.LinesJson).HasColumnType("jsonb");
        b.HasIndex(x => new { x.TenantId, x.ClientRequestId }).IsUnique();
        b.HasIndex(x => x.BranchId);
    }
}

internal class RecipeLineConfiguration : IEntityTypeConfiguration<RecipeLine>
{
    public void Configure(EntityTypeBuilder<RecipeLine> b)
    {
        b.Property(x => x.Quantity).HasPrecision(18, 4);
        b.Property(x => x.EnteredQuantity).HasPrecision(18, 3);
        b.Property(x => x.EnteredUnit).HasMaxLength(30);
        b.HasIndex(x => x.MenuItemId);
        b.HasIndex(x => x.ModifierOptionId);
        b.HasOne<InventoryItem>().WithMany().HasForeignKey(x => x.InventoryItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<MenuItem>().WithMany().HasForeignKey(x => x.MenuItemId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<ModifierOption>().WithMany().HasForeignKey(x => x.ModifierOptionId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal class DealConfiguration : IEntityTypeConfiguration<Deal>
{
    public void Configure(EntityTypeBuilder<Deal> b)
    {
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.NameUr).HasMaxLength(100);
        b.Property(x => x.Description).HasMaxLength(300);
        b.Property(x => x.CouponCode).HasMaxLength(30);
        b.Property(x => x.Percent).HasPrecision(6, 3);
        b.Property(x => x.GetPercentOff).HasPrecision(6, 3);
        b.HasIndex(x => x.TenantId);
        b.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.DealId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Branches).WithOne().HasForeignKey(x => x.DealId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal class DealItemConfiguration : IEntityTypeConfiguration<DealItem>
{
    public void Configure(EntityTypeBuilder<DealItem> b)
    {
        b.HasOne<MenuItem>().WithMany().HasForeignKey(x => x.MenuItemId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal class DealBranchConfiguration : IEntityTypeConfiguration<DealBranch>
{
    public void Configure(EntityTypeBuilder<DealBranch> b)
    {
        b.HasKey(x => new { x.DealId, x.BranchId });
    }
}

internal class OrderDealApplicationConfiguration : IEntityTypeConfiguration<OrderDealApplication>
{
    public void Configure(EntityTypeBuilder<OrderDealApplication> b)
    {
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.HasIndex(x => x.OrderId);
    }
}

internal class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> b)
    {
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Phone).HasMaxLength(30);
        b.Property(x => x.Address).HasMaxLength(300);
        b.Property(x => x.Notes).HasMaxLength(300);
        b.HasIndex(x => new { x.TenantId, x.Phone }).IsUnique().HasFilter("\"Phone\" IS NOT NULL");
    }
}

internal class SupplierConfiguration : IEntityTypeConfiguration<Supplier>
{
    public void Configure(EntityTypeBuilder<Supplier> b)
    {
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.ContactName).HasMaxLength(100);
        b.Property(x => x.Phone).HasMaxLength(30);
        b.Property(x => x.Address).HasMaxLength(300);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
    }
}

internal class PurchaseOrderConfiguration : IEntityTypeConfiguration<PurchaseOrder>
{
    public void Configure(EntityTypeBuilder<PurchaseOrder> b)
    {
        b.Property(x => x.Number).HasMaxLength(20).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.Total).HasPrecision(18, 2);
        b.Property(x => x.Version).IsRowVersion();
        b.HasIndex(x => new { x.BranchId, x.Sequence }).IsUnique();
        b.HasIndex(x => new { x.BranchId, x.Status });
        b.HasOne<Supplier>().WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.PurchaseOrderId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal class PurchaseOrderLineConfiguration : IEntityTypeConfiguration<PurchaseOrderLine>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderLine> b)
    {
        b.Property(x => x.Quantity).HasPrecision(18, 3);
        b.Property(x => x.Unit).HasMaxLength(30).IsRequired();
        b.Property(x => x.UnitCost).HasPrecision(18, 4);
        b.Property(x => x.QuantityBase).HasPrecision(18, 3);
        b.Property(x => x.ReceivedBase).HasPrecision(18, 3);
        b.HasOne<InventoryItem>().WithMany().HasForeignKey(x => x.InventoryItemId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal class StockTransferConfiguration : IEntityTypeConfiguration<StockTransfer>
{
    public void Configure(EntityTypeBuilder<StockTransfer> b)
    {
        b.Property(x => x.Number).HasMaxLength(20).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(300);
        b.Property(x => x.TotalCost).HasPrecision(18, 2);
        b.Property(x => x.Version).IsRowVersion();
        b.HasIndex(x => new { x.TenantId, x.Sequence }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.ClientRequestId }).IsUnique();
        b.HasIndex(x => x.FromBranchId);
        b.HasIndex(x => x.ToBranchId);
        b.HasOne<Branch>().WithMany().HasForeignKey(x => x.FromBranchId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Branch>().WithMany().HasForeignKey(x => x.ToBranchId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.StockTransferId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal class StockTransferLineConfiguration : IEntityTypeConfiguration<StockTransferLine>
{
    public void Configure(EntityTypeBuilder<StockTransferLine> b)
    {
        b.Property(x => x.Quantity).HasPrecision(18, 3);
        b.Property(x => x.UnitCost).HasPrecision(18, 4);
        b.HasOne<InventoryItem>().WithMany().HasForeignKey(x => x.InventoryItemId).OnDelete(DeleteBehavior.Restrict);
    }
}
