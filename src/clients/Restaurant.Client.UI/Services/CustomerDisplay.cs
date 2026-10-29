namespace Restaurant.Client.UI.Services;

/// <summary>What the customer-facing screen shows: the restaurant while idle, the order as it's rung up, then the change.</summary>
/// <param name="Status">"idle", "cart" or "paid".</param>
public record CustomerDisplayState(
    string Status, string BusinessName, string? LogoUrl, string PrimaryColor, string CurrencyCode, string Language,
    IReadOnlyList<CustomerDisplayLine> Lines, decimal Subtotal, decimal Tax, decimal ServiceCharge, decimal Discount, decimal Total,
    decimal? Paid = null, decimal? Change = null, string? OrderNumber = null);

public record CustomerDisplayLine(int Quantity, string Name, string? Detail, decimal Total);

/// <summary>
/// The POS publishes here; the customer display page listens. In the desktop app both windows share this
/// singleton (one process). In the browser the display is another tab or window, so the POS also sends the
/// state over a BroadcastChannel (see restaurant-ui.js), which the display page subscribes to.
/// </summary>
public sealed class CustomerDisplay
{
    public const string Channel = "rp-customer-display";

    public CustomerDisplayState? State { get; private set; }

    public event Action<CustomerDisplayState>? Changed;

    public void Publish(CustomerDisplayState state)
    {
        State = state;
        Changed?.Invoke(state);
    }
}
