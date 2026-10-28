using Restaurant.Contracts.Enums;

namespace Restaurant.Contracts.Branding;

public record ReceiptTemplateDto(Guid Id, string Code, string Name, string? Description, ReceiptPaperSize PaperSize, ReceiptKind Kind);

/// <summary>Everything that makes receipts and the app look like this restaurant (Tailor Book's model).</summary>
/// <param name="ReceiptLanguage">"en" or "ur".</param>
public record BrandingDto(
    string? LogoUrl,
    string PrimaryColor,
    string SecondaryColor,
    string AccentColor,
    string TextColor,
    string BackgroundColor,
    string? DisplayName,
    string? Tagline,
    string? DisplayAddress,
    string? DisplayPhone,
    string? DisplayWhatsAppNumber,
    string? FooterText,
    string? ReceiptHeader,
    string? ReceiptFooter,
    string ReceiptLanguage,
    string? SocialLinksJson,
    Guid? DefaultReceiptTemplateId,
    Guid? DefaultKitchenTemplateId,
    IReadOnlyList<ReceiptTemplateDto> AvailableTemplates);

public record UpdateBrandingRequest(
    string PrimaryColor,
    string SecondaryColor,
    string AccentColor,
    string TextColor,
    string BackgroundColor,
    string? DisplayName,
    string? Tagline,
    string? DisplayAddress,
    string? DisplayPhone,
    string? DisplayWhatsAppNumber,
    string? FooterText,
    string? ReceiptHeader,
    string? ReceiptFooter,
    string ReceiptLanguage,
    string? SocialLinksJson,
    Guid? DefaultReceiptTemplateId,
    Guid? DefaultKitchenTemplateId);
