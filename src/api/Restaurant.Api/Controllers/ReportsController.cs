using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Restaurant.Api.Auth;
using Restaurant.Application.Features.Reports;
using Restaurant.Contracts.Auth;
using Restaurant.Contracts.Reports;

namespace Restaurant.Api.Controllers;

/// <summary>
/// Reports for the selected branch (X-Branch-Id), or every branch with allBranches=true. The filter is in
/// the query string: from, to (business dates), sessionId, orderType, cashierId, paymentMethodId.
/// The day's closing report stays at /api/sessions/{id}/report.
/// </summary>
[ApiController]
[Route("api/reports")]
[Authorize]
public class ReportsController(ReportService reports) : ControllerBase
{
    [HttpGet("sales")]
    [RequirePermission(Permissions.Reports.Sales, Permissions.Reports.Financial)]
    public Task<SalesReportDto> Sales([FromQuery] ReportFilter filter, CancellationToken ct) => reports.SalesAsync(filter, ct);

    [HttpGet("orders")]
    [RequirePermission(Permissions.Reports.Sales, Permissions.Reports.Financial)]
    public Task<OrdersReportDto> Orders([FromQuery] ReportFilter filter, CancellationToken ct) => reports.OrdersAsync(filter, ct);

    [HttpGet("products")]
    [RequirePermission(Permissions.Reports.Sales, Permissions.Reports.Financial)]
    public Task<ProductsReportDto> Products([FromQuery] ReportFilter filter, CancellationToken ct) => reports.ProductsAsync(filter, ct);

    [HttpGet("payments")]
    [RequirePermission(Permissions.Reports.Financial)]
    public Task<PaymentsReportDto> Payments([FromQuery] ReportFilter filter, CancellationToken ct) => reports.PaymentsAsync(filter, ct);

    [HttpGet("inventory")]
    [RequirePermission(Permissions.Reports.Inventory)]
    public Task<InventoryReportDto> Inventory([FromQuery] ReportFilter filter, CancellationToken ct) => reports.InventoryAsync(filter, ct);

    [HttpGet("staff")]
    [RequirePermission(Permissions.Reports.Sales, Permissions.Reports.Financial)]
    public Task<IReadOnlyList<ReportStaffDto>> Staff(CancellationToken ct) => reports.StaffAsync(ct);
}
