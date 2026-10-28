using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Restaurant.Api.Auth;
using Restaurant.Application.Features.Sessions;
using Restaurant.Contracts.Auth;
using Restaurant.Contracts.Sessions;

namespace Restaurant.Api.Controllers;

/// <summary>The selected branch's business day (X-Branch-Id) and its cash drawers.</summary>
[ApiController]
[Route("api/sessions")]
[Authorize]
public class SessionsController(SessionService sessions) : ControllerBase
{
    /// <summary>The open business day, or 204 when the day hasn't been started.</summary>
    [HttpGet("current")]
    public async Task<ActionResult<BusinessSessionDto>> Current(CancellationToken ct) =>
        await sessions.GetCurrentAsync(ct) is { } session ? session : NoContent();

    [HttpPost("start")]
    [RequirePermission(Permissions.Sessions.Start)]
    public Task<BusinessSessionDto> Start(StartSessionRequest request, CancellationToken ct) => sessions.StartAsync(request, ct);

    [HttpPost("current/registers")]
    [RequirePermission(Permissions.Sessions.Start)]
    public Task<BusinessSessionDto> OpenRegister(OpenRegisterRequest request, CancellationToken ct) => sessions.OpenRegisterAsync(request, ct);

    [HttpPost("current/cash-movements")]
    [RequirePermission(Permissions.Sessions.CashMovements)]
    public Task<CashMovementDto> RecordCash(RecordCashMovementRequest request, CancellationToken ct) => sessions.RecordCashMovementAsync(request, ct);

    [HttpGet("{id:guid}/cash-movements")]
    [RequirePermission(Permissions.Sessions.CashMovements, Permissions.Sessions.Close)]
    public Task<IReadOnlyList<CashMovementDto>> CashMovements(Guid id, CancellationToken ct) => sessions.ListCashMovementsAsync(id, ct);

    [HttpGet("current/close-preview")]
    [RequirePermission(Permissions.Sessions.Close)]
    public Task<ClosePreviewDto> ClosePreview(CancellationToken ct) => sessions.ClosePreviewAsync(ct);

    [HttpPost("current/close")]
    [RequirePermission(Permissions.Sessions.Close)]
    public Task<ClosedSessionDto> Close(CloseSessionRequest request, CancellationToken ct) => sessions.CloseAsync(request, ct);

    [HttpPost("{id:guid}/reopen")]
    [RequirePermission(Permissions.Sessions.Reopen)]
    public Task<BusinessSessionDto> Reopen(Guid id, ReopenSessionRequest request, CancellationToken ct) => sessions.ReopenAsync(id, request, ct);

    [HttpGet("history")]
    [RequirePermission(Permissions.Reports.Sales, Permissions.Sessions.Close)]
    public Task<IReadOnlyList<SessionHistoryItemDto>> History(CancellationToken ct) => sessions.HistoryAsync(ct);

    /// <summary>A day's closing report (printable).</summary>
    [HttpGet("{id:guid}/report")]
    [RequirePermission(Permissions.Reports.Sales, Permissions.Sessions.Close)]
    public Task<ClosedSessionDto> Report(Guid id, CancellationToken ct) => sessions.GetReportAsync(id, ct);
}
