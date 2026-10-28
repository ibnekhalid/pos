using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Restaurant.Api.Auth;
using Restaurant.Application.Features.Approvals;
using Restaurant.Application.Features.Kitchen;
using Restaurant.Application.Features.Orders;
using Restaurant.Application.Features.Payments;
using Restaurant.Application.Features.Receipts;
using Restaurant.Contracts.Auth;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Kitchen;
using Restaurant.Contracts.Orders;
using Restaurant.Contracts.Receipts;

namespace Restaurant.Api.Controllers;

[ApiController]
[Route("api")]
[Authorize]
public class OrdersController(OrderService orders, PaymentService payments, ReceiptService receipts, ApprovalService approvals) : ControllerBase
{
    private const string SeeOrders = Permissions.Orders.Create;

    [HttpPost("orders/price-preview")]
    [RequirePermission(Permissions.Orders.Create, Permissions.Orders.Edit)]
    public Task<PriceBreakdownDto> Preview(PricePreviewRequest request, CancellationToken ct) => orders.PreviewAsync(request, ct);

    [HttpGet("orders")]
    [RequirePermission(SeeOrders, Permissions.Orders.Edit, Permissions.Payments.Take)]
    public Task<IReadOnlyList<OrderSummaryDto>> List([FromQuery] string? scope, CancellationToken ct) => orders.ListAsync(scope, ct);

    [HttpGet("orders/{id:guid}")]
    [RequirePermission(SeeOrders, Permissions.Orders.Edit, Permissions.Payments.Take)]
    public Task<OrderDto> Get(Guid id, CancellationToken ct) => orders.GetAsync(id, ct);

    [HttpPost("orders")]
    [RequirePermission(Permissions.Orders.Create)]
    public Task<OrderDto> Create(CreateOrderRequest request, CancellationToken ct) => orders.CreateAsync(request, ct);

    [HttpPost("orders/{id:guid}/lines")]
    [RequirePermission(Permissions.Orders.Edit)]
    public Task<OrderDto> AddLines(Guid id, AddOrderLinesRequest request, CancellationToken ct) => orders.AddLinesAsync(id, request, ct);

    [HttpPost("orders/{id:guid}/send")]
    [RequirePermission(Permissions.Orders.SendToKitchen)]
    public Task<OrderDto> Send(Guid id, CancellationToken ct) => orders.SendToKitchenAsync(id, ct);

    /// <summary>Removes a line; after the kitchen has it this is a void (orders.void or approval, and a reason).</summary>
    [HttpPost("orders/{id:guid}/lines/{lineId:guid}/void")]
    [RequirePermission(Permissions.Orders.Edit)]
    public Task<OrderDto> VoidLine(Guid id, Guid lineId, ReasonRequest request, CancellationToken ct) => orders.VoidLineAsync(id, lineId, request, ct);

    [HttpPost("orders/{id:guid}/cancel")]
    [RequirePermission(Permissions.Orders.RequestCancel)]
    public Task<OrderDto> Cancel(Guid id, ReasonRequest request, CancellationToken ct) => orders.CancelAsync(id, request, ct);

    [HttpPost("orders/{id:guid}/void")]
    [RequirePermission(Permissions.Orders.RequestCancel, Permissions.Orders.Void)]
    public Task<OrderDto> Void(Guid id, ReasonRequest request, CancellationToken ct) => orders.VoidAsync(id, request, ct);

    [HttpPut("orders/{id:guid}/discount")]
    [RequirePermission(Permissions.Orders.Edit)]
    public Task<OrderDto> Discount(Guid id, SetDiscountRequest request, CancellationToken ct) => orders.SetDiscountAsync(id, request, ct);

    [HttpPost("orders/{id:guid}/served")]
    [RequirePermission(Permissions.Orders.Edit, Permissions.Kitchen.Update)]
    public Task<OrderDto> Served(Guid id, CancellationToken ct) => orders.MarkServedAsync(id, ct);

    [HttpPost("orders/{id:guid}/payments")]
    [RequirePermission(Permissions.Payments.Take)]
    public Task<OrderDto> Pay(Guid id, TakePaymentRequest request, CancellationToken ct) => payments.TakeAsync(id, request, ct);

    [HttpPost("payments/{paymentId:guid}/refunds")]
    [RequirePermission(Permissions.Payments.Take, Permissions.Payments.Refund)]
    public Task<OrderDto> Refund(Guid paymentId, RefundRequest request, CancellationToken ct) => payments.RefundAsync(paymentId, request, ct);

    /// <summary>Staff who could approve <paramref name="permission"/> here (names only, for the approval prompt).</summary>
    [HttpGet("approvers")]
    public Task<IReadOnlyList<ApproverDto>> Approvers([FromQuery] string permission, CancellationToken ct) => approvals.ApproversAsync(permission, ct);

    // ---------------------------------------------------------------- receipts

    [HttpGet("orders/{id:guid}/receipt")]
    [RequirePermission(Permissions.Payments.PrintReceipts, Permissions.Orders.SendToKitchen)]
    public Task<ReceiptDocumentDto> Receipt(Guid id, [FromQuery] PrintKind kind = PrintKind.Receipt, [FromQuery] Guid? ticketId = null, CancellationToken ct = default) =>
        receipts.PreviewAsync(id, kind, ticketId, ct);

    /// <summary>Logs a print and returns the document to print (marked as a reprint after the first copy).</summary>
    [HttpPost("orders/{id:guid}/receipt/print")]
    [RequirePermission(Permissions.Payments.PrintReceipts, Permissions.Orders.SendToKitchen)]
    public Task<ReceiptDocumentDto> Print(Guid id, PrintReceiptRequest request, CancellationToken ct) => receipts.PrintAsync(id, request, ct);

    [HttpGet("orders/{id:guid}/receipt/pdf")]
    [RequirePermission(Permissions.Payments.PrintReceipts)]
    public async Task<IActionResult> Pdf(Guid id, [FromQuery] PrintKind kind = PrintKind.Receipt, [FromQuery] Guid? ticketId = null, CancellationToken ct = default)
    {
        var (content, fileName) = await receipts.PdfAsync(id, kind, ticketId, ct);
        Response.Headers.CacheControl = "no-store";
        Response.Headers.ContentDisposition = $"inline; filename=\"{fileName}\"";
        return File(content, "application/pdf");
    }
}

[ApiController]
[Route("api/kitchen")]
[Authorize]
public class KitchenController(KitchenService kitchen) : ControllerBase
{
    [HttpGet("tickets")]
    [RequirePermission(Permissions.Kitchen.View)]
    public Task<IReadOnlyList<KitchenTicketDto>> Tickets([FromQuery] Guid? stationId, CancellationToken ct) => kitchen.ListAsync(stationId, ct);

    [HttpPut("tickets/{id:guid}/status")]
    [RequirePermission(Permissions.Kitchen.Update)]
    public async Task<IActionResult> UpdateStatus(Guid id, UpdateTicketStatusRequest request, CancellationToken ct)
    {
        await kitchen.UpdateStatusAsync(id, request, ct);
        return NoContent();
    }
}
