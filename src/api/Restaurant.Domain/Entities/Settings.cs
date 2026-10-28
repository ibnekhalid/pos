using Restaurant.Contracts.Enums;
using Restaurant.Domain.Common;

namespace Restaurant.Domain.Entities;

/// <summary>A tax the restaurant charges. Rates are data, never hard-coded (they differ by province).</summary>
public class TaxRate : TenantEntity
{
    public string Name { get; set; } = string.Empty;
    public decimal Percent { get; set; }

    /// <summary>True when menu prices already include this tax.</summary>
    public bool IsInclusive { get; set; }

    public OrderTypes AppliesTo { get; set; } = OrderTypes.All;
    public TaxPaymentScope PaymentScope { get; set; } = TaxPaymentScope.Any;

    /// <summary>Null applies to every branch.</summary>
    public Guid? BranchId { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>A way customers can pay. Card and wallets are recorded only — no gateway is integrated.</summary>
public class PaymentMethodConfig : TenantEntity
{
    public string Name { get; set; } = string.Empty;
    public PaymentMethodKind Kind { get; set; }

    /// <summary>Counts toward the drawer's expected cash.</summary>
    public bool IsCash { get; set; }

    /// <summary>The cashier must enter a reference (card slip, transaction id).</summary>
    public bool RequiresReference { get; set; }

    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}
