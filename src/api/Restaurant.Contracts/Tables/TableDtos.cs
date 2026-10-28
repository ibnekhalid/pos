namespace Restaurant.Contracts.Tables;

public record DiningAreaDto(Guid Id, string Name, int SortOrder);
public record SaveDiningAreaRequest(string Name, int SortOrder);

/// <param name="OpenOrderId">The table's open dine-in order, if any (the table is occupied).</param>
/// <param name="NeedsCleaning">Free, but an order on it was closed since it was last marked clean.</param>
/// <param name="ReservedAt">The next booking on this table that starts within the next two hours (or is running now).</param>
public record TableDto(Guid Id, Guid? AreaId, string Number, int Seats, bool IsActive, Guid? OpenOrderId, string? OpenOrderNumber,
    bool NeedsCleaning = false, Guid? ReservationId = null, string? ReservedFor = null, DateTimeOffset? ReservedAt = null);
public record SaveTableRequest(Guid? AreaId, string Number, int Seats, bool IsActive = true);

public record ReservationDto(Guid Id, string CustomerName, string? Phone, int PartySize, DateTimeOffset At, int DurationMinutes, Guid? TableId,
    string? TableNumber, string? Notes, Enums.ReservationStatus Status, DateTimeOffset? SeatedAt, string? CreatedByName);

/// <param name="DurationMinutes">How long the table is held (default 90).</param>
public record SaveReservationRequest(string CustomerName, string? Phone, int PartySize, DateTimeOffset At, int DurationMinutes, Guid? TableId, string? Notes);

/// <param name="TableId">Where they sit; null keeps the booked table.</param>
public record SeatReservationRequest(Guid? TableId);
