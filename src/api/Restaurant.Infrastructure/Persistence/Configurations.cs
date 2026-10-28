using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Restaurant.Domain.Constants;
using Restaurant.Domain.Entities;

namespace Restaurant.Infrastructure.Persistence;

// Money is numeric(18,2), percentages numeric(6,3). Enums are stored as integers.

internal class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> b)
    {
        b.Property(x => x.FullName).HasMaxLength(100).IsRequired();
        // Email and mobile number are both sign-in names, so each identifies at most one account.
        b.HasIndex(x => x.NormalizedEmail).IsUnique().HasFilter("\"NormalizedEmail\" IS NOT NULL");
        b.HasIndex(x => x.PhoneNumber).IsUnique().HasFilter("\"PhoneNumber\" IS NOT NULL");
    }
}

internal class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> b)
    {
        b.Property(x => x.Name).HasMaxLength(150).IsRequired();
        b.Property(x => x.LegalName).HasMaxLength(200);
        b.Property(x => x.NtnNumber).HasMaxLength(30);
        b.Property(x => x.StrnNumber).HasMaxLength(30);
        b.Property(x => x.CurrencyCode).HasMaxLength(3).IsRequired();
    }
}

internal class BranchConfiguration : IEntityTypeConfiguration<Branch>
{
    public void Configure(EntityTypeBuilder<Branch> b)
    {
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Address).HasMaxLength(300);
        b.Property(x => x.City).HasMaxLength(100);
        b.Property(x => x.Phone).HasMaxLength(30);
        b.Property(x => x.OrderNumberPrefix).HasMaxLength(5).IsRequired();
        b.Property(x => x.TimeZoneId).HasMaxLength(64).IsRequired();
        b.Property(x => x.ServiceChargePercent).HasPrecision(6, 3);
        b.Property(x => x.CashDifferenceThreshold).HasPrecision(18, 2);
        b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal class RegisterConfiguration : IEntityTypeConfiguration<Register>
{
    public void Configure(EntityTypeBuilder<Register> b)
    {
        b.Property(x => x.Name).HasMaxLength(60).IsRequired();
        b.Property(x => x.ReceiptPrinterName).HasMaxLength(200);
        b.Property(x => x.KitchenPrinterName).HasMaxLength(200);
        b.HasIndex(x => x.TenantId);
        // Exactly one default register per branch.
        b.HasIndex(x => x.BranchId).IsUnique().HasFilter("\"IsDefault\"").HasDatabaseName("IX_Registers_BranchId_Default");
        b.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal class StaffMembershipConfiguration : IEntityTypeConfiguration<StaffMembership>
{
    public void Configure(EntityTypeBuilder<StaffMembership> b)
    {
        b.ToTable("Memberships");
        b.Property(x => x.DisplayName).HasMaxLength(100).IsRequired();
        b.Property(x => x.PinHash).HasMaxLength(200);
        b.Property(x => x.InviteCodeHash).HasMaxLength(64);
        b.HasIndex(x => new { x.TenantId, x.UserId }).IsUnique();
        b.HasIndex(x => x.UserId);
        b.HasIndex(x => x.InviteCodeHash).IsUnique().HasFilter("\"InviteCodeHash\" IS NOT NULL");
        b.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Roles).WithOne().HasForeignKey(x => x.MembershipId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Branches).WithOne().HasForeignKey(x => x.MembershipId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal class MembershipRoleConfiguration : IEntityTypeConfiguration<MembershipRole>
{
    public void Configure(EntityTypeBuilder<MembershipRole> b)
    {
        b.HasKey(x => new { x.MembershipId, x.RoleId });
        b.HasIndex(x => x.RoleId);
        b.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal class MembershipBranchConfiguration : IEntityTypeConfiguration<MembershipBranch>
{
    public void Configure(EntityTypeBuilder<MembershipBranch> b)
    {
        b.HasKey(x => new { x.MembershipId, x.BranchId });
        b.HasIndex(x => x.BranchId);
        b.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal class TenantRoleConfiguration : IEntityTypeConfiguration<TenantRole>
{
    public void Configure(EntityTypeBuilder<TenantRole> b)
    {
        b.ToTable("TenantRoles");
        b.Property(x => x.Name).HasMaxLength(60).IsRequired();
        b.Property(x => x.Description).HasMaxLength(300);
        b.Property(x => x.SystemKey).HasMaxLength(30);
        b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Permissions).WithOne().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> b)
    {
        b.HasKey(x => new { x.RoleId, x.Permission });
        b.Property(x => x.Permission).HasMaxLength(60);
    }
}

internal class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        b.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
        b.Property(x => x.ReplacedByTokenHash).HasMaxLength(64);
        b.Property(x => x.CreatedByIp).HasMaxLength(64);
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.HasIndex(x => x.UserId);
        b.HasIndex(x => x.MembershipId);
        b.HasIndex(x => x.DeviceId);
        b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal class RegisteredDeviceConfiguration : IEntityTypeConfiguration<RegisteredDevice>
{
    public void Configure(EntityTypeBuilder<RegisteredDevice> b)
    {
        b.ToTable("Devices");
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.HasIndex(x => x.TenantId);
        b.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal class TaxRateConfiguration : IEntityTypeConfiguration<TaxRate>
{
    public void Configure(EntityTypeBuilder<TaxRate> b)
    {
        b.Property(x => x.Name).HasMaxLength(60).IsRequired();
        b.Property(x => x.Percent).HasPrecision(6, 3);
        b.HasIndex(x => x.TenantId);
        b.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal class PaymentMethodConfiguration : IEntityTypeConfiguration<PaymentMethodConfig>
{
    public void Configure(EntityTypeBuilder<PaymentMethodConfig> b)
    {
        b.ToTable("PaymentMethods");
        b.Property(x => x.Name).HasMaxLength(60).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
    }
}

internal class BrandingSettingsConfiguration : IEntityTypeConfiguration<BrandingSettings>
{
    public void Configure(EntityTypeBuilder<BrandingSettings> b)
    {
        foreach (var color in new[] { nameof(BrandingSettings.PrimaryColor), nameof(BrandingSettings.SecondaryColor), nameof(BrandingSettings.AccentColor), nameof(BrandingSettings.TextColor), nameof(BrandingSettings.BackgroundColor) })
        {
            b.Property(color).HasMaxLength(9).IsRequired();
        }
        b.Property(x => x.DisplayName).HasMaxLength(150);
        b.Property(x => x.Tagline).HasMaxLength(200);
        b.Property(x => x.DisplayAddress).HasMaxLength(300);
        b.Property(x => x.DisplayPhone).HasMaxLength(30);
        b.Property(x => x.DisplayWhatsAppNumber).HasMaxLength(30);
        b.Property(x => x.FooterText).HasMaxLength(300);
        b.Property(x => x.ReceiptHeader).HasMaxLength(500);
        b.Property(x => x.ReceiptFooter).HasMaxLength(500);
        b.Property(x => x.ReceiptLanguage).HasMaxLength(5).IsRequired();
        b.Property(x => x.SocialLinksJson).HasMaxLength(2000);
        b.HasIndex(x => x.TenantId).IsUnique();
        b.HasOne(x => x.LogoFile).WithMany().HasForeignKey(x => x.LogoFileId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<ReceiptTemplate>().WithMany().HasForeignKey(x => x.DefaultReceiptTemplateId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<ReceiptTemplate>().WithMany().HasForeignKey(x => x.DefaultKitchenTemplateId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal class ReceiptTemplateConfiguration : IEntityTypeConfiguration<ReceiptTemplate>
{
    public void Configure(EntityTypeBuilder<ReceiptTemplate> b)
    {
        b.Property(x => x.Code).HasMaxLength(40).IsRequired();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Description).HasMaxLength(300);
        b.HasIndex(x => x.Code).IsUnique().HasFilter("\"TenantId\" IS NULL");
        b.HasData(SystemReceiptTemplates.All);
    }
}

internal class FileAssetConfiguration : IEntityTypeConfiguration<FileAsset>
{
    public void Configure(EntityTypeBuilder<FileAsset> b)
    {
        b.Property(x => x.OwnerType).HasMaxLength(40).IsRequired();
        b.Property(x => x.StorageKey).HasMaxLength(300).IsRequired();
        b.Property(x => x.FileName).HasMaxLength(200).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
        b.HasIndex(x => x.TenantId);
    }
}

internal class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> b)
    {
        b.Property(x => x.Action).HasMaxLength(60).IsRequired();
        b.Property(x => x.EntityType).HasMaxLength(60).IsRequired();
        b.Property(x => x.EntityId).HasMaxLength(64);
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.BeforeJson).HasColumnType("jsonb");
        b.Property(x => x.AfterJson).HasColumnType("jsonb");
        b.Property(x => x.IpAddress).HasMaxLength(64);
        b.HasIndex(x => new { x.TenantId, x.CreatedAt });
    }
}
