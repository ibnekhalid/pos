using Restaurant.Client.Core.Api;
using Restaurant.Client.Core.Auth;
using Restaurant.Contracts.Branding;
using Restaurant.Contracts.Tenancy;

namespace Restaurant.Client.Core;

/// <summary>
/// The restaurant's colours, logo, name and currency — applied as CSS variables so every screen is
/// themed by the restaurant, never by hard-coded colours (Tailor Book's approach, same rule as receipts).
/// </summary>
public class BrandingState
{
    public const string DefaultPrimary = "#B91C1C";
    public const string DefaultAccent = "#F59E0B";

    private readonly RestaurantApi _api;

    public BrandingState(RestaurantApi api, SessionStore session)
    {
        _api = api;
        session.SessionEnded += () =>
        {
            Branding = null;
            Tenant = null;
            Changed?.Invoke();
            return Task.CompletedTask;
        };
    }

    public BrandingDto? Branding { get; private set; }
    public TenantDto? Tenant { get; private set; }

    public string Primary => Branding?.PrimaryColor ?? DefaultPrimary;
    public string Secondary => Branding?.SecondaryColor ?? "#374151";
    public string Accent => Branding?.AccentColor ?? DefaultAccent;
    public string? LogoUrl => _api.ResolveUrl(Branding?.LogoUrl);
    public string DisplayName => Branding?.DisplayName ?? Tenant?.Name ?? "Restaurant POS";
    public string CurrencyCode => Tenant?.CurrencyCode ?? "PKR";

    public event Action? Changed;

    /// <summary>CSS custom properties for the app root element.</summary>
    public string CssVariables =>
        $"--rp-primary:{Primary};--rp-primary-soft:{Format.WithAlpha(Primary, 0.12)};--rp-secondary:{Secondary};" +
        $"--rp-accent:{Accent};--rp-accent-soft:{Format.WithAlpha(Accent, 0.18)};";

    public string Money(decimal amount) => Format.Money(amount, CurrencyCode);

    public async Task LoadAsync()
    {
        try
        {
            var branding = _api.GetBrandingAsync();
            var tenant = _api.GetTenantAsync();
            Branding = await branding;
            Tenant = await tenant;
            Changed?.Invoke();
        }
        catch
        {
            // Offline or signed out: keep the default theme.
        }
    }

    public void Apply(BrandingDto branding)
    {
        Branding = branding;
        Changed?.Invoke();
    }

    public void Apply(TenantDto tenant)
    {
        Tenant = tenant;
        Changed?.Invoke();
    }
}
