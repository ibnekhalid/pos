using Microsoft.AspNetCore.Components;
using Restaurant.Client.Core;
using Restaurant.Client.Core.Localization;

namespace Restaurant.Client.UI.Components;

/// <summary>
/// Base for screens and components: re-renders when the language, branding or signed-in user
/// changes, and offers the common helpers (T for strings, Money for amounts).
/// </summary>
public abstract class RpComponent : ComponentBase, IDisposable
{
    [Inject] protected Localizer L { get; set; } = default!;
    [Inject] protected BrandingState Brand { get; set; } = default!;
    [Inject] protected UserContext User { get; set; } = default!;
    [Inject] protected NavigationManager Nav { get; set; } = default!;

    protected string T(string key) => L.T(key);
    protected string T(string key, params (string Name, string Value)[] args) => L.T(key, args);
    protected string Money(decimal amount) => Brand.Money(amount);

    protected override void OnInitialized()
    {
        L.Changed += Refresh;
        Brand.Changed += Refresh;
        User.Changed += Refresh;
    }

    protected void Refresh() => InvokeAsync(StateHasChanged);

    public virtual void Dispose()
    {
        L.Changed -= Refresh;
        Brand.Changed -= Refresh;
        User.Changed -= Refresh;
        GC.SuppressFinalize(this);
    }
}
