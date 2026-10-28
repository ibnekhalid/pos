using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Restaurant.Application.Common.Exceptions;
using Restaurant.Application.Common.Interfaces;
using Restaurant.Application.Common.Services;
using Restaurant.Application.Features.Orders;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Receipts;
using Restaurant.Domain.Entities;

namespace Restaurant.Application.Features.Receipts;

/// <summary>
/// Builds the resolved receipt / kitchen-ticket document (PLAN.md section 10) and logs prints: the second
/// print of the same receipt or ticket is marked as a reprint, and printing never creates a sale (T11).
/// </summary>
public class ReceiptService(
    IApplicationDbContext db,
    ITenantContext tenant,
    OrderService orders,
    IFileStorageService files,
    IReceiptPdfRenderer pdf,
    TimeProvider clock)
{
    public async Task<ReceiptDocumentDto> PreviewAsync(Guid orderId, PrintKind kind, Guid? ticketId, CancellationToken ct)
    {
        var order = await orders.LoadOrderAsync(orderId, ct);
        return await BuildAsync(order, kind, await TicketAsync(order, kind, ticketId, ct), isReprint: false, copy: 0, ct);
    }

    /// <summary>Logs a print and returns what to print (marked as a reprint after the first copy).</summary>
    public async Task<ReceiptDocumentDto> PrintAsync(Guid orderId, PrintReceiptRequest request, CancellationToken ct)
    {
        var order = await orders.LoadOrderAsync(orderId, ct);
        var ticket = await TicketAsync(order, request.Kind, request.TicketId, ct);

        var printedTicketId = ticket?.Id;
        var copy = await db.ReceiptPrints.CountAsync(p => p.OrderId == order.Id && p.Kind == request.Kind && p.TicketId == printedTicketId, ct) + 1;
        db.ReceiptPrints.Add(new ReceiptPrint
        {
            TenantId = order.TenantId,
            BranchId = order.BranchId,
            OrderId = order.Id,
            Kind = request.Kind,
            TicketId = ticket?.Id,
            CopyNumber = copy,
            PrintedByUserId = tenant.RequireUserId()
        });
        await db.SaveChangesAsync(ct);
        return await BuildAsync(order, request.Kind, ticket, isReprint: copy > 1, copy, ct);
    }

    public async Task<(byte[] Content, string FileName)> PdfAsync(Guid orderId, PrintKind kind, Guid? ticketId, CancellationToken ct)
    {
        var order = await orders.LoadOrderAsync(orderId, ct);
        var document = await BuildAsync(order, kind, await TicketAsync(order, kind, ticketId, ct), isReprint: false, copy: 0, ct);

        byte[]? logo = null;
        var branding = await db.BrandingSettings.FirstOrDefaultAsync(ct);
        if (branding?.LogoFileId is { } logoId && await db.FileAssets.FirstOrDefaultAsync(f => f.Id == logoId, ct) is { } asset)
        {
            logo = await files.ReadAllBytesAsync(asset.StorageKey, ct);
        }
        return (pdf.Render(document, logo), $"{(kind == PrintKind.Receipt ? "receipt" : "kitchen")}-{order.OrderNumber}.pdf");
    }

    private async Task<KitchenTicket?> TicketAsync(Order order, PrintKind kind, Guid? ticketId, CancellationToken ct)
    {
        if (kind != PrintKind.KitchenTicket) return null;
        var query = db.KitchenTickets.Include(t => t.Items).Where(t => t.OrderId == order.Id);
        return ticketId is { } id
            ? await query.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Kitchen ticket")
            : await query.OrderByDescending(t => t.Round).FirstOrDefaultAsync(ct) ?? throw new ConflictException("This order hasn't been sent to the kitchen.");
    }

    private async Task<ReceiptDocumentDto> BuildAsync(Order order, PrintKind kind, KitchenTicket? ticket, bool isReprint, int copy, CancellationToken ct)
    {
        var branch = await db.Branches.FirstAsync(b => b.Id == order.BranchId, ct);
        var tenantRow = await db.Tenants.FirstAsync(t => t.Id == order.TenantId, ct);
        var branding = await db.BrandingSettings.FirstOrDefaultAsync(ct) ?? new BrandingSettings { DisplayName = tenantRow.Name };
        var session = await db.BusinessSessions.FirstAsync(s => s.Id == order.BusinessSessionId, ct);

        var templateId = kind == PrintKind.Receipt ? branding.DefaultReceiptTemplateId : branding.DefaultKitchenTemplateId;
        var paper = templateId is { } id
            ? await db.ReceiptTemplates.Where(t => t.Id == id).Select(t => (ReceiptPaperSize?)t.PaperSize).FirstOrDefaultAsync(ct) ?? ReceiptPaperSize.Thermal80mm
            : ReceiptPaperSize.Thermal80mm;

        string? logoUrl = null;
        if (branding.LogoFileId is { } logoId && await db.FileAssets.FirstOrDefaultAsync(f => f.Id == logoId, ct) is { } logo)
        {
            logoUrl = files.GetPublicUrl(logo.StorageKey);
        }

        var zone = CallerExtensions.TimeZone(branch.TimeZoneId);
        var printedAt = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).ToString("dd MMM yyyy, h:mm tt", CultureInfo.InvariantCulture);
        var payments = await db.Payments.Where(p => p.OrderId == order.Id).OrderBy(p => p.CreatedAt).ToListAsync(ct);
        var station = ticket?.StationId is { } stationId ? await db.KitchenStations.Where(s => s.Id == stationId).Select(s => s.Name).FirstOrDefaultAsync(ct) : null;

        var lines = ticket is not null
            ? ticket.Items.Select(i => new ReceiptLineDto(i.Quantity, i.Name, i.ModifiersText?.Split(", ").ToList() ?? [], i.Notes, 0, i.Cancelled)).ToList()
            : order.Lines.Where(l => l.Status == OrderLineStatus.Active).OrderBy(l => l.CreatedAt)
                .Select(l => new ReceiptLineDto(l.Quantity, l.VariantName is null ? l.Name : $"{l.Name} ({l.VariantName})",
                    l.Modifiers.Select(m => m.PriceDelta == 0 ? m.Name : $"{m.Name} +{m.PriceDelta:0.##}").ToList(), l.Notes, l.LineTotal, false)).ToList();

        return new ReceiptDocumentDto(
            kind, paper, branding.ReceiptLanguage, isReprint, copy,
            branding.DisplayName ?? tenantRow.Name, branding.Tagline, branch.Name, branding.DisplayAddress ?? branch.Address,
            branding.DisplayPhone ?? branch.Phone, tenantRow.NtnNumber, tenantRow.StrnNumber, branding.ReceiptHeader,
            branding.ReceiptFooter ?? branding.FooterText, branding.PrimaryColor, branding.AccentColor, logoUrl, tenantRow.CurrencyCode,
            order.OrderNumber, order.Type, order.TableLabel, session.BusinessDate.ToString("dd MMM yyyy", CultureInfo.InvariantCulture), printedAt,
            order.CreatedByName, order.CustomerName, lines, order.Subtotal, order.DiscountTotal, order.DiscountReason, order.ServiceCharge,
            order.ServiceChargeApplies ? order.ServiceChargePercent : 0, OrderPricing.TaxLines(order), order.RoundingAdjustment, order.Total,
            payments.Select(p => new ReceiptPaymentDto(p.MethodName, p.Amount, p.Tendered, p.Change, p.Reference)).ToList(),
            order.RefundedTotal, order.PaidTotal, order.Balance, station, ticket?.Round, order.Notes,
            ticket is null ? OrderPricing.AppliedDeals(order).Where(a => a.Discount > 0).ToList() : null);
    }
}
