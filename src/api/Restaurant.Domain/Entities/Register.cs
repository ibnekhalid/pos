using Restaurant.Domain.Common;

namespace Restaurant.Domain.Entities;

/// <summary>A till / cash drawer in a branch. Each branch has one default register.</summary>
public class Register : BranchEntity
{
    public string Name { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Windows printer name the desktop app sends receipts to.</summary>
    public string? ReceiptPrinterName { get; set; }

    public string? KitchenPrinterName { get; set; }
    public bool CashDrawerEnabled { get; set; }
}
