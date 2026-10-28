using Microsoft.EntityFrameworkCore;
using Restaurant.Application.Common.Exceptions;
using Restaurant.Application.Common.Interfaces;
using Restaurant.Application.Common.Services;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Tables;
using Restaurant.Domain.Entities;

namespace Restaurant.Application.Features.Tables;

/// <summary>
/// Table bookings for the selected branch. A booking holds its table for DurationMinutes from At; two
/// bookings can't hold the same table at the same time. Seating one only records it: the order is started
/// at the POS as usual.
/// </summary>
public class ReservationService(IApplicationDbContext db, ITenantContext tenant, IRealtimeNotifier notifier, TimeProvider clock)
{
    /// <summary>The bookings starting on this local date (the branch's time zone), any status.</summary>
    public async Task<IReadOnlyList<ReservationDto>> ListAsync(DateOnly date, CancellationToken ct)
    {
        var branchId = tenant.RequireBranchId();
        var zone = await ZoneAsync(branchId, ct);
        var start = Utc(date, zone);
        var end = Utc(date.AddDays(1), zone);
        var rows = await db.Reservations.Where(r => r.BranchId == branchId && r.At >= start && r.At < end).OrderBy(r => r.At).ToListAsync(ct);
        return await ToDtosAsync(rows, ct);
    }

    public async Task<ReservationDto> SaveAsync(Guid? id, SaveReservationRequest request, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        var branchId = tenant.RequireBranchId();
        var name = request.CustomerName?.Trim() ?? "";
        if (name.Length is 0 or > 100) throw new ValidationAppException(nameof(request.CustomerName), "Enter the guest's name (up to 100 characters).");
        if (request.Phone?.Length > 30) throw new ValidationAppException(nameof(request.Phone), "The phone number is too long.");
        if (request.PartySize is < 1 or > 200) throw new ValidationAppException(nameof(request.PartySize), "The party size must be between 1 and 200.");
        if (request.DurationMinutes is < 15 or > 720) throw new ValidationAppException(nameof(request.DurationMinutes), "Hold the table for 15 minutes to 12 hours.");
        if (request.Notes?.Length > 300) throw new ValidationAppException(nameof(request.Notes), "The note is too long.");

        var reservation = id is null ? new Reservation { TenantId = tenantId, BranchId = branchId }
            : await db.Reservations.FirstOrDefaultAsync(r => r.Id == id && r.BranchId == branchId, ct) ?? throw new NotFoundException("Reservation");
        if (id is not null && reservation.Status != ReservationStatus.Booked) throw new ConflictException("Only an upcoming booking can be changed.");
        if (id is null && request.At < clock.GetUtcNow().AddMinutes(-30)) throw new ValidationAppException(nameof(request.At), "That time has already passed.");

        reservation.CustomerName = name;
        reservation.Phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim();
        reservation.PartySize = request.PartySize;
        reservation.At = request.At.ToUniversalTime();
        reservation.DurationMinutes = request.DurationMinutes;
        reservation.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        reservation.TableId = request.TableId;
        if (request.TableId is { } tableId) await EnsureTableFreeAsync(reservation, tableId, ct);

        if (id is null) db.Reservations.Add(reservation);
        await db.SaveChangesAsync(ct);
        await notifier.OrdersChangedAsync(branchId); // the table map shows bookings
        return (await ToDtosAsync([reservation], ct))[0];
    }

    /// <summary>The guests arrived. They may sit at another table than the one booked.</summary>
    public async Task<ReservationDto> SeatAsync(Guid id, SeatReservationRequest request, CancellationToken ct)
    {
        var reservation = await LoadBookedAsync(id, ct);
        if (request.TableId is { } tableId && tableId != reservation.TableId)
        {
            reservation.TableId = tableId;
            await EnsureTableFreeAsync(reservation, tableId, ct);
        }
        reservation.Status = ReservationStatus.Seated;
        reservation.SeatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await notifier.OrdersChangedAsync(reservation.BranchId);
        return (await ToDtosAsync([reservation], ct))[0];
    }

    /// <summary>Cancelled by the guest, or a no-show: either way the table is free again.</summary>
    public async Task<ReservationDto> CloseAsync(Guid id, ReservationStatus status, CancellationToken ct)
    {
        if (status is not (ReservationStatus.Cancelled or ReservationStatus.NoShow)) throw new ValidationAppException("Status", "Cancel it or mark it as a no-show.");
        var reservation = await LoadBookedAsync(id, ct);
        reservation.Status = status;
        await db.SaveChangesAsync(ct);
        await notifier.OrdersChangedAsync(reservation.BranchId);
        return (await ToDtosAsync([reservation], ct))[0];
    }

    private async Task<Reservation> LoadBookedAsync(Guid id, CancellationToken ct)
    {
        var branchId = tenant.RequireBranchId();
        var reservation = await db.Reservations.FirstOrDefaultAsync(r => r.Id == id && r.BranchId == branchId, ct) ?? throw new NotFoundException("Reservation");
        if (reservation.Status != ReservationStatus.Booked) throw new ConflictException("This booking is no longer upcoming.");
        return reservation;
    }

    /// <summary>The table must be this branch's and not held by another booking for any of this booking's time.</summary>
    private async Task EnsureTableFreeAsync(Reservation reservation, Guid tableId, CancellationToken ct)
    {
        var table = await db.Tables.FirstOrDefaultAsync(t => t.Id == tableId && t.BranchId == reservation.BranchId && t.IsActive, ct)
            ?? throw new NotFoundException("Table");
        var start = reservation.At;
        var end = reservation.At.AddMinutes(reservation.DurationMinutes);
        var clashes = await db.Reservations
            .Where(r => r.TableId == tableId && r.Id != reservation.Id && (r.Status == ReservationStatus.Booked || r.Status == ReservationStatus.Seated)
                && r.At < end && r.At > start.AddHours(-12))
            .ToListAsync(ct);
        if (clashes.FirstOrDefault(r => r.At.AddMinutes(r.DurationMinutes) > start) is { } clash)
        {
            throw new ConflictException($"Table {table.Number} is booked for {clash.CustomerName} then.");
        }
    }

    private async Task<List<ReservationDto>> ToDtosAsync(List<Reservation> rows, CancellationToken ct)
    {
        var tableIds = rows.Where(r => r.TableId is not null).Select(r => r.TableId!.Value).Distinct().ToList();
        var tables = await db.Tables.Where(t => tableIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Number, ct);
        var userIds = rows.Select(r => r.CreatedBy).Where(u => u is not null).Distinct().ToList();
        var names = await db.Memberships.Where(m => userIds.Contains(m.UserId)).ToDictionaryAsync(m => m.UserId, m => m.DisplayName, ct);
        return rows.Select(r => new ReservationDto(r.Id, r.CustomerName, r.Phone, r.PartySize, r.At, r.DurationMinutes, r.TableId,
            r.TableId is { } t ? tables.GetValueOrDefault(t) : null, r.Notes, r.Status, r.SeatedAt,
            r.CreatedBy is { } by ? names.GetValueOrDefault(by) : null)).ToList();
    }

    private async Task<TimeZoneInfo> ZoneAsync(Guid branchId, CancellationToken ct) =>
        CallerExtensions.TimeZone(await db.Branches.Where(b => b.Id == branchId).Select(b => b.TimeZoneId).FirstAsync(ct));

    private static DateTimeOffset Utc(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}
