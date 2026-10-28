using Restaurant.Contracts.Enums;
using Restaurant.Domain.Entities;

namespace Restaurant.Domain.Constants;

/// <summary>Receipt layouts that ship with the product (seeded by migration; fixed ids).</summary>
public static class SystemReceiptTemplates
{
    public static readonly Guid Thermal80mmId = new("0192f0a0-0000-7000-8000-000000000001");
    public static readonly Guid Thermal58mmId = new("0192f0a0-0000-7000-8000-000000000002");
    public static readonly Guid A4InvoiceId = new("0192f0a0-0000-7000-8000-000000000003");
    public static readonly Guid Kitchen80mmId = new("0192f0a0-0000-7000-8000-000000000004");

    private static readonly DateTimeOffset Seeded = new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);

    public static IReadOnlyList<ReceiptTemplate> All { get; } =
    [
        Create(Thermal80mmId, "thermal_80mm", "Thermal 80 mm", "Standard counter receipt printer.", ReceiptPaperSize.Thermal80mm, ReceiptKind.CustomerReceipt),
        Create(Thermal58mmId, "thermal_58mm", "Thermal 58 mm", "Narrow receipt printers and handheld printers.", ReceiptPaperSize.Thermal58mm, ReceiptKind.CustomerReceipt),
        Create(A4InvoiceId, "a4_invoice", "A4 invoice", "Full-page invoice for corporate customers.", ReceiptPaperSize.A4, ReceiptKind.CustomerReceipt),
        Create(Kitchen80mmId, "kitchen_80mm", "Kitchen ticket 80 mm", "Large item text, no prices.", ReceiptPaperSize.Thermal80mm, ReceiptKind.KitchenTicket)
    ];

    private static ReceiptTemplate Create(Guid id, string code, string name, string description, ReceiptPaperSize paperSize, ReceiptKind kind) => new()
    {
        Id = id,
        CreatedAt = Seeded,
        Code = code,
        Name = name,
        Description = description,
        PaperSize = paperSize,
        Kind = kind,
        IsSystem = true,
        IsActive = true
    };
}
