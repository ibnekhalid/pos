using Restaurant.Contracts.Enums;
using Restaurant.Domain.Common;

namespace Restaurant.Domain.Entities;

/// <summary>One restaurant business. Everything else (branches, staff, menu, orders...) hangs off it.</summary>
public class Tenant : Entity
{
    public string Name { get; set; } = string.Empty;
    public string? LegalName { get; set; }

    /// <summary>National Tax Number, printed on receipts.</summary>
    public string? NtnNumber { get; set; }

    /// <summary>Sales Tax Registration Number, printed on receipts.</summary>
    public string? StrnNumber { get; set; }

    public string CurrencyCode { get; set; } = "PKR";
    public TenantStatus Status { get; set; } = TenantStatus.Active;
}
