using Restaurant.Contracts.Enums;
using Restaurant.Domain.Common;

namespace Restaurant.Domain.Entities;

/// <summary>
/// One per tenant. Everything a printed receipt or the themed app needs to look like this
/// restaurant rather than any other — never hard-code these values (Tailor Book's model).
/// </summary>
public class BrandingSettings : TenantEntity
{
    public Guid? LogoFileId { get; set; }
    public FileAsset? LogoFile { get; set; }

    public string PrimaryColor { get; set; } = "#B91C1C";
    public string SecondaryColor { get; set; } = "#374151";
    public string AccentColor { get; set; } = "#F59E0B";
    public string TextColor { get; set; } = "#111827";
    public string BackgroundColor { get; set; } = "#FFFFFF";

    public string? DisplayName { get; set; }
    public string? Tagline { get; set; }
    public string? DisplayAddress { get; set; }
    public string? DisplayPhone { get; set; }
    public string? DisplayWhatsAppNumber { get; set; }
    public string? FooterText { get; set; }
    public string? ReceiptHeader { get; set; }
    public string? ReceiptFooter { get; set; }

    /// <summary>"en" or "ur".</summary>
    public string ReceiptLanguage { get; set; } = "en";

    public string? SocialLinksJson { get; set; }

    public Guid? DefaultReceiptTemplateId { get; set; }
    public Guid? DefaultKitchenTemplateId { get; set; }
}

/// <summary>
/// A receipt layout. System templates (TenantId == null) ship with the product; a tenant picks its
/// defaults in BrandingSettings.
/// </summary>
public class ReceiptTemplate : Entity
{
    public Guid? TenantId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public ReceiptPaperSize PaperSize { get; set; }
    public ReceiptKind Kind { get; set; }
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Metadata for an uploaded file (logo, menu photo). The bytes live in IFileStorageService
/// (local disk now, blob storage later) — never in Postgres.
/// </summary>
public class FileAsset : TenantEntity
{
    public string OwnerType { get; set; } = string.Empty;
    public Guid? OwnerId { get; set; }
    public string StorageKey { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
}
