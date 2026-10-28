using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Restaurant.Api.Auth;
using Restaurant.Application.Features.Tables;
using Restaurant.Contracts.Auth;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Tables;

namespace Restaurant.Api.Controllers;

/// <summary>Table bookings of the selected branch (front of house: orders.create).</summary>
[ApiController]
[Route("api/reservations")]
[Authorize]
[RequirePermission(Permissions.Orders.Create)]
public class ReservationsController(ReservationService reservations) : ControllerBase
{
    /// <summary>The bookings on a date (the branch's local date).</summary>
    [HttpGet]
    public Task<IReadOnlyList<ReservationDto>> List([FromQuery] DateOnly date, CancellationToken ct) => reservations.ListAsync(date, ct);

    [HttpPost]
    public Task<ReservationDto> Create(SaveReservationRequest request, CancellationToken ct) => reservations.SaveAsync(null, request, ct);

    [HttpPut("{id:guid}")]
    public Task<ReservationDto> Update(Guid id, SaveReservationRequest request, CancellationToken ct) => reservations.SaveAsync(id, request, ct);

    [HttpPost("{id:guid}/seat")]
    public Task<ReservationDto> Seat(Guid id, SeatReservationRequest request, CancellationToken ct) => reservations.SeatAsync(id, request, ct);

    [HttpPost("{id:guid}/cancel")]
    public Task<ReservationDto> Cancel(Guid id, CancellationToken ct) => reservations.CloseAsync(id, ReservationStatus.Cancelled, ct);

    [HttpPost("{id:guid}/no-show")]
    public Task<ReservationDto> NoShow(Guid id, CancellationToken ct) => reservations.CloseAsync(id, ReservationStatus.NoShow, ct);
}
