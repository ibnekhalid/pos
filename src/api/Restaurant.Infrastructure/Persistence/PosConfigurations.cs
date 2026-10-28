using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Restaurant.Domain.Entities;

namespace Restaurant.Infrastructure.Persistence;

internal class KitchenStationConfiguration : IEntityTypeConfiguration<KitchenStation>
{
    public void Configure(EntityTypeBuilder<KitchenStation> b)
    {
        b.Property(x => x.Name).HasMaxLength(60).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
    }
}

internal class MenuCategoryConfiguration : IEntityTypeConfiguration<MenuCategory>
{
    public void Configure(EntityTypeBuilder<MenuCategory> b)
    {
        b.Property(x => x.Name).HasMaxLength(60).IsRequired();
        b.Property(x => x.NameUr).HasMaxLength(60);
        b.Property(x => x.Color).HasMaxLength(9);
        b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
    }
}

internal class MenuItemConfiguration : IEntityTypeConfiguration<MenuItem>
{
    public void Configure(EntityTypeBuilder<MenuItem> b)
    {
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.NameUr).HasMaxLength(100);
        b.Property(x => x.Description).HasMaxLength(300);
        b.Property(x => x.Sku).HasMaxLength(40);
        b.HasIndex(x => x.TenantId);
        b.HasOne<MenuCategory>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<KitchenStation>().WithMany().HasForeignKey(x => x.KitchenStationId).OnDelete(DeleteBehavior.SetNull);
        b.HasMany(x => x.Variants).WithOne().HasForeignKey(x => x.MenuItemId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.ModifierGroups).WithOne().HasForeignKey(x => x.MenuItemId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal class MenuItemVariantConfiguration : IEntityTypeConfiguration<MenuItemVariant>
{
    public void Configure(EntityTypeBuilder<MenuItemVariant> b)
    {
        b.Property(x => x.Name).HasMaxLength(60).IsRequired();
        b.Property(x => x.NameUr).HasMaxLength(60);
        b.Property(x => x.Sku).HasMaxLength(40);
    }
}

internal class ModifierGroupConfiguration : IEntityTypeConfiguration<ModifierGroup>
{
    public void Configure(EntityTypeBuilder<ModifierGroup> b)
    {
        b.Property(x => x.Name).HasMaxLength(60).IsRequired();
        b.Property(x => x.NameUr).HasMaxLength(60);
        b.HasIndex(x => x.TenantId);
        b.HasMany(x => x.Options).WithOne().HasForeignKey(x => x.ModifierGroupId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal class ModifierOptionConfiguration : IEntityTypeConfiguration<ModifierOption>
{
    public void Configure(EntityTypeBuilder<ModifierOption> b)
    {
        b.Property(x => x.Name).HasMaxLength(60).IsRequired();
        b.Property(x => x.NameUr).HasMaxLength(60);
    }
}

internal class MenuItemModifierGroupConfiguration : IEntityTypeConfiguration<MenuItemModifierGroup>
{
    public void Configure(EntityTypeBuilder<MenuItemModifierGroup> b)
    {
        b.HasKey(x => new { x.MenuItemId, x.ModifierGroupId });
        b.HasOne<ModifierGroup>().WithMany().HasForeignKey(x => x.ModifierGroupId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal class BranchMenuItemConfiguration : IEntityTypeConfiguration<BranchMenuItem>
{
    public void Configure(EntityTypeBuilder<BranchMenuItem> b)
    {
        b.HasIndex(x => new { x.BranchId, x.MenuItemId }).IsUnique();
        b.HasOne<MenuItem>().WithMany().HasForeignKey(x => x.MenuItemId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal class DiningAreaConfiguration : IEntityTypeConfiguration<DiningArea>
{
    public void Configure(EntityTypeBuilder<DiningArea> b)
    {
        b.Property(x => x.Name).HasMaxLength(60).IsRequired();
        b.HasIndex(x => new { x.BranchId, x.Name }).IsUnique();
        b.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal class RestaurantTableConfiguration : IEntityTypeConfiguration<RestaurantTable>
{
    public void Configure(EntityTypeBuilder<RestaurantTable> b)
    {
        b.ToTable("Tables");
        b.Property(x => x.Number).HasMaxLength(10).IsRequired();
        b.HasIndex(x => new { x.BranchId, x.Number }).IsUnique();
        b.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<DiningArea>().WithMany().HasForeignKey(x => x.AreaId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal class BusinessSessionConfiguration : IEntityTypeConfiguration<BusinessSession>
{
    public void Configure(EntityTypeBuilder<BusinessSession> b)
    {
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.ReopenReason).HasMaxLength(500);
        b.Property(x => x.ClosingSummaryJson).HasColumnType("jsonb");
        // At most one open business day per branch, even if two people press Start at once (T3).
        b.HasIndex(x => x.BranchId).IsUnique().HasFilter("\"Status\" = 0").HasDatabaseName("IX_BusinessSessions_OneOpenPerBranch");
        b.HasIndex(x => new { x.BranchId, x.BusinessDate });
        b.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Registers).WithOne().HasForeignKey(x => x.BusinessSessionId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal class RegisterSessionConfiguration : IEntityTypeConfiguration<RegisterSession>
{
    public void Configure(EntityTypeBuilder<RegisterSession> b)
    {
        b.Property(x => x.DifferenceReason).HasMaxLength(500);
        b.HasIndex(x => x.RegisterId).IsUnique().HasFilter("\"Status\" = 0").HasDatabaseName("IX_RegisterSessions_OneOpenPerRegister");
        b.HasOne<Register>().WithMany().HasForeignKey(x => x.RegisterId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal class CashMovementConfiguration : IEntityTypeConfiguration<CashMovement>
{
    public void Configure(EntityTypeBuilder<CashMovement> b)
    {
        b.Property(x => x.Reason).HasMaxLength(300);
        b.HasIndex(x => x.RegisterSessionId);
        b.HasIndex(x => new { x.TenantId, x.ClientRequestId }).IsUnique().HasFilter("\"ClientRequestId\" IS NOT NULL");
        b.HasOne<RegisterSession>().WithMany().HasForeignKey(x => x.RegisterSessionId).OnDelete(DeleteBehavior.Restrict);
        b.Ignore(x => x.SignedAmount);
    }
}

internal class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> b)
    {
        b.Property(x => x.OrderNumber).HasMaxLength(20).IsRequired();
        b.Property(x => x.TableLabel).HasMaxLength(10);
        b.Property(x => x.CustomerName).HasMaxLength(100);
        b.Property(x => x.CustomerPhone).HasMaxLength(30);
        b.Property(x => x.Notes).HasMaxLength(300);
        b.Property(x => x.CreatedByName).HasMaxLength(100);
        b.Property(x => x.DiscountReason).HasMaxLength(200);
        b.Property(x => x.VoidReason).HasMaxLength(300);
        b.Property(x => x.ServiceChargePercent).HasPrecision(6, 3);
        b.Property(x => x.DiscountPercent).HasPrecision(6, 3);
        b.Property(x => x.TaxRatesJson).HasColumnType("jsonb");
        b.Property(x => x.TaxBreakdownJson).HasColumnType("jsonb");
        b.Property(x => x.Version).IsRowVersion();
        b.HasIndex(x => new { x.TenantId, x.ClientRequestId }).IsUnique();
        b.HasIndex(x => new { x.BranchId, x.BusinessSessionId });
        b.HasIndex(x => new { x.BranchId, x.Status });
        b.HasIndex(x => x.TableId);
        b.HasOne<BusinessSession>().WithMany().HasForeignKey(x => x.BusinessSessionId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.CouponCode).HasMaxLength(30);
        b.Property(x => x.DealRulesJson).HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
        b.Property(x => x.AutoDealsJson).HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
        b.HasIndex(x => x.CustomerId);
        b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.SetNull);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.DealApplications).WithOne().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.Ignore(x => x.IsOpen);
        b.Ignore(x => x.Balance);
    }
}

internal class OrderLineConfiguration : IEntityTypeConfiguration<OrderLine>
{
    public void Configure(EntityTypeBuilder<OrderLine> b)
    {
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.NameUr).HasMaxLength(100);
        b.Property(x => x.VariantName).HasMaxLength(60);
        b.Property(x => x.Sku).HasMaxLength(40);
        b.Property(x => x.Notes).HasMaxLength(200);
        b.Property(x => x.VoidReason).HasMaxLength(300);
        b.HasMany(x => x.Modifiers).WithOne().HasForeignKey(x => x.OrderLineId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal class OrderLineModifierConfiguration : IEntityTypeConfiguration<OrderLineModifier>
{
    public void Configure(EntityTypeBuilder<OrderLineModifier> b)
    {
        b.Property(x => x.GroupName).HasMaxLength(60).IsRequired();
        b.Property(x => x.Name).HasMaxLength(60).IsRequired();
    }
}

internal class OrderStatusHistoryConfiguration : IEntityTypeConfiguration<OrderStatusHistory>
{
    public void Configure(EntityTypeBuilder<OrderStatusHistory> b)
    {
        b.ToTable("OrderStatusHistory");
        b.Property(x => x.Reason).HasMaxLength(300);
        b.HasIndex(x => x.OrderId);
    }
}

internal class OrderSequenceConfiguration : IEntityTypeConfiguration<OrderSequence>
{
    public void Configure(EntityTypeBuilder<OrderSequence> b)
    {
        b.ToTable("OrderSequences");
        b.HasKey(x => new { x.BranchId, x.BusinessDate });
    }
}

internal class KitchenTicketConfiguration : IEntityTypeConfiguration<KitchenTicket>
{
    public void Configure(EntityTypeBuilder<KitchenTicket> b)
    {
        b.Property(x => x.OrderNumber).HasMaxLength(20).IsRequired();
        b.Property(x => x.TableLabel).HasMaxLength(10);
        b.HasIndex(x => new { x.BranchId, x.Status });
        b.HasIndex(x => x.OrderId);
        b.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal class KitchenTicketItemConfiguration : IEntityTypeConfiguration<KitchenTicketItem>
{
    public void Configure(EntityTypeBuilder<KitchenTicketItem> b)
    {
        b.Property(x => x.Name).HasMaxLength(170).IsRequired();
        b.Property(x => x.ModifiersText).HasMaxLength(500);
        b.Property(x => x.Notes).HasMaxLength(200);
    }
}

internal class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> b)
    {
        b.Property(x => x.MethodName).HasMaxLength(60).IsRequired();
        b.Property(x => x.Reference).HasMaxLength(100);
        b.Property(x => x.TakenByName).HasMaxLength(100);
        b.HasIndex(x => new { x.TenantId, x.ClientRequestId }).IsUnique();
        b.HasIndex(x => x.OrderId);
        b.HasIndex(x => x.RegisterSessionId);
        b.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<RegisterSession>().WithMany().HasForeignKey(x => x.RegisterSessionId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal class RefundConfiguration : IEntityTypeConfiguration<Refund>
{
    public void Configure(EntityTypeBuilder<Refund> b)
    {
        b.Property(x => x.Reason).HasMaxLength(300).IsRequired();
        b.Property(x => x.ByName).HasMaxLength(100);
        b.HasIndex(x => new { x.TenantId, x.ClientRequestId }).IsUnique();
        b.HasIndex(x => x.OrderId);
        b.HasIndex(x => x.RegisterSessionId);
        b.HasOne<Payment>().WithMany().HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal class ReceiptPrintConfiguration : IEntityTypeConfiguration<ReceiptPrint>
{
    public void Configure(EntityTypeBuilder<ReceiptPrint> b)
    {
        b.HasIndex(x => new { x.OrderId, x.Kind });
    }
}
