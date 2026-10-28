using Restaurant.Contracts.Branding;

namespace Restaurant.Client.UI;

internal static class BrandingExtensions
{
    /// <summary>The owner has changed something from the defaults a new restaurant starts with.</summary>
    public static bool UpdatedByOwner(this BrandingDto branding) =>
        !string.Equals(branding.PrimaryColor, "#B91C1C", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrWhiteSpace(branding.ReceiptFooter)
        || !string.IsNullOrWhiteSpace(branding.Tagline);
}
