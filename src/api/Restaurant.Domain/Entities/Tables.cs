using Restaurant.Domain.Common;

namespace Restaurant.Domain.Entities;

public class DiningArea : BranchEntity
{
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}

/// <summary>
/// A table. "Occupied" is not stored: it is the table's open dine-in order. "Needs cleaning" is derived too:
/// an order on it was closed after CleanedAt.
/// </summary>
public class RestaurantTable : BranchEntity
{
    public Guid? AreaId { get; set; }
    public string Number { get; set; } = string.Empty;
    public int Seats { get; set; } = 4;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? CleanedAt { get; set; }
}

/// <summary>A booking. The table is optional until the guests are seated.</summary>
public class Reservation : BranchEntity
{
    public string CustomerName { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public int PartySize { get; set; } = 2;
    public DateTimeOffset At { get; set; }
    public int DurationMinutes { get; set; } = 90;
    public Guid? TableId { get; set; }
    public string? Notes { get; set; }
    public Restaurant.Contracts.Enums.ReservationStatus Status { get; set; } = Restaurant.Contracts.Enums.ReservationStatus.Booked;
    public DateTimeOffset? SeatedAt { get; set; }
}
