using System.Text.RegularExpressions;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Restaurant.Application.Common.Exceptions;
using Restaurant.Application.Common.Extensions;
using Restaurant.Application.Common.Interfaces;
using Restaurant.Application.Common.Services;
using Restaurant.Contracts.Branding;
using Restaurant.Domain.Entities;

namespace Restaurant.Application.Features.Branding;

public static partial class FileUploadPolicy
{
    public const long MaxLogoSizeBytes = 5 * 1024 * 1024;

    public static readonly HashSet<string> AllowedImageContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp"
    };

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    public static partial Regex HexColor();
}

public class UpdateBrandingRequestValidator : AbstractValidator<UpdateBrandingRequest>
{
    public UpdateBrandingRequestValidator()
    {
        RuleFor(x => x.PrimaryColor).Must(IsHexColor).WithMessage("Use a colour like #B91C1C.");
        RuleFor(x => x.SecondaryColor).Must(IsHexColor).WithMessage("Use a colour like #B91C1C.");
        RuleFor(x => x.AccentColor).Must(IsHexColor).WithMessage("Use a colour like #B91C1C.");
        RuleFor(x => x.TextColor).Must(IsHexColor).WithMessage("Use a colour like #B91C1C.");
        RuleFor(x => x.BackgroundColor).Must(IsHexColor).WithMessage("Use a colour like #B91C1C.");
        RuleFor(x => x.DisplayName).MaximumLength(150);
        RuleFor(x => x.Tagline).MaximumLength(200);
        RuleFor(x => x.DisplayAddress).MaximumLength(300);
        RuleFor(x => x.DisplayPhone).MaximumLength(30);
        RuleFor(x => x.DisplayWhatsAppNumber).MaximumLength(30);
        RuleFor(x => x.FooterText).MaximumLength(300);
        RuleFor(x => x.ReceiptHeader).MaximumLength(500);
        RuleFor(x => x.ReceiptFooter).MaximumLength(500);
        RuleFor(x => x.ReceiptLanguage).Must(l => l is "en" or "ur").WithMessage("Choose English or Urdu.");
        RuleFor(x => x.SocialLinksJson).MaximumLength(2000);
    }

    private static bool IsHexColor(string? color) => color is not null && FileUploadPolicy.HexColor().IsMatch(color);
}

/// <summary>Ported from Tailor Book: colours, logo, receipt texts and default receipt templates.</summary>
public class BrandingService(
    IApplicationDbContext db,
    ITenantContext tenant,
    IFileStorageService fileStorage,
    AuditLogger audit,
    IValidator<UpdateBrandingRequest> validator)
{
    public async Task<BrandingDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var branding = await GetOrCreateAsync(cancellationToken);
        return await ToDtoAsync(branding, cancellationToken);
    }

    public async Task<BrandingDto> UpdateAsync(UpdateBrandingRequest request, CancellationToken cancellationToken = default)
    {
        await validator.ValidateAndThrowAppExceptionAsync(request, cancellationToken);
        var branding = await GetOrCreateAsync(cancellationToken);

        await EnsureTemplateAsync(request.DefaultReceiptTemplateId, cancellationToken);
        await EnsureTemplateAsync(request.DefaultKitchenTemplateId, cancellationToken);

        var before = await ToDtoAsync(branding, cancellationToken);
        branding.PrimaryColor = request.PrimaryColor.ToUpperInvariant();
        branding.SecondaryColor = request.SecondaryColor.ToUpperInvariant();
        branding.AccentColor = request.AccentColor.ToUpperInvariant();
        branding.TextColor = request.TextColor.ToUpperInvariant();
        branding.BackgroundColor = request.BackgroundColor.ToUpperInvariant();
        branding.DisplayName = request.DisplayName?.Trim();
        branding.Tagline = request.Tagline?.Trim();
        branding.DisplayAddress = request.DisplayAddress?.Trim();
        branding.DisplayPhone = request.DisplayPhone?.Trim();
        branding.DisplayWhatsAppNumber = request.DisplayWhatsAppNumber?.Trim();
        branding.FooterText = request.FooterText?.Trim();
        branding.ReceiptHeader = request.ReceiptHeader?.Trim();
        branding.ReceiptFooter = request.ReceiptFooter?.Trim();
        branding.ReceiptLanguage = request.ReceiptLanguage;
        branding.SocialLinksJson = request.SocialLinksJson;
        branding.DefaultReceiptTemplateId = request.DefaultReceiptTemplateId;
        branding.DefaultKitchenTemplateId = request.DefaultKitchenTemplateId;
        branding.UpdatedAt = DateTimeOffset.UtcNow;

        var after = await ToDtoAsync(branding, cancellationToken);
        audit.Record(AuditActions.BrandingUpdated, nameof(BrandingSettings), branding.Id, before with { AvailableTemplates = [] }, after with { AvailableTemplates = [] });
        await db.SaveChangesAsync(cancellationToken);
        return after;
    }

    public async Task<BrandingDto> UpdateLogoAsync(Stream content, string fileName, string contentType, long sizeBytes, CancellationToken cancellationToken = default)
    {
        if (!FileUploadPolicy.AllowedImageContentTypes.Contains(contentType))
        {
            throw new ValidationAppException("logo", "Only JPEG, PNG or WEBP images are allowed.");
        }
        if (sizeBytes <= 0 || sizeBytes > FileUploadPolicy.MaxLogoSizeBytes)
        {
            throw new ValidationAppException("logo", "The logo must be 5 MB or smaller.");
        }

        var tenantId = tenant.RequireTenantId();
        var branding = await GetOrCreateAsync(cancellationToken);
        var previousLogoId = branding.LogoFileId;

        var stored = await fileStorage.SaveAsync(content, Path.GetFileName(fileName), contentType, $"branding/{tenantId}", cancellationToken);
        var asset = new FileAsset
        {
            TenantId = tenantId,
            OwnerType = "Logo",
            OwnerId = branding.Id,
            StorageKey = stored.StorageKey,
            FileName = Path.GetFileName(fileName),
            ContentType = contentType,
            SizeBytes = sizeBytes
        };
        db.FileAssets.Add(asset);
        branding.LogoFileId = asset.Id;
        branding.UpdatedAt = DateTimeOffset.UtcNow;

        audit.Record(AuditActions.LogoUpdated, nameof(BrandingSettings), branding.Id);
        await db.SaveChangesAsync(cancellationToken);

        if (previousLogoId is not null)
        {
            var previous = await db.FileAssets.FirstOrDefaultAsync(f => f.Id == previousLogoId, cancellationToken);
            if (previous is not null)
            {
                await fileStorage.DeleteAsync(previous.StorageKey, cancellationToken);
                db.FileAssets.Remove(previous);
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        return await ToDtoAsync(branding, cancellationToken);
    }

    public async Task<IReadOnlyList<ReceiptTemplateDto>> ListTemplatesAsync(CancellationToken cancellationToken = default)
    {
        tenant.RequireTenantId();
        return await db.ReceiptTemplates
            .Where(t => t.IsActive)
            .OrderBy(t => t.Kind).ThenBy(t => t.Name)
            .Select(t => new ReceiptTemplateDto(t.Id, t.Code, t.Name, t.Description, t.PaperSize, t.Kind))
            .ToListAsync(cancellationToken);
    }

    private async Task EnsureTemplateAsync(Guid? templateId, CancellationToken cancellationToken)
    {
        // The query filter already limits this to system templates and the tenant's own.
        if (templateId is { } id && !await db.ReceiptTemplates.AnyAsync(t => t.Id == id && t.IsActive, cancellationToken))
        {
            throw new NotFoundException("Receipt template");
        }
    }

    private async Task<BrandingSettings> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var branding = await db.BrandingSettings.FirstOrDefaultAsync(cancellationToken);
        if (branding is not null)
        {
            return branding;
        }

        branding = new BrandingSettings { TenantId = tenantId };
        db.BrandingSettings.Add(branding);
        await db.SaveChangesAsync(cancellationToken);
        return branding;
    }

    private async Task<BrandingDto> ToDtoAsync(BrandingSettings b, CancellationToken cancellationToken)
    {
        string? logoUrl = null;
        if (b.LogoFileId is not null)
        {
            var logo = await db.FileAssets.FirstOrDefaultAsync(f => f.Id == b.LogoFileId, cancellationToken);
            logoUrl = logo is null ? null : fileStorage.GetPublicUrl(logo.StorageKey);
        }

        return new BrandingDto(
            logoUrl, b.PrimaryColor, b.SecondaryColor, b.AccentColor, b.TextColor, b.BackgroundColor,
            b.DisplayName, b.Tagline, b.DisplayAddress, b.DisplayPhone, b.DisplayWhatsAppNumber,
            b.FooterText, b.ReceiptHeader, b.ReceiptFooter, b.ReceiptLanguage, b.SocialLinksJson,
            b.DefaultReceiptTemplateId, b.DefaultKitchenTemplateId,
            await ListTemplatesAsync(cancellationToken));
    }
}
