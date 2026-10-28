namespace Restaurant.Contracts.Tables;

public record DiningAreaDto(Guid Id, string Name, int SortOrder);
public record SaveDiningAreaRequest(string Name, int SortOrder);

/// <param name="OpenOrderId">The table's open dine-in order, if any (the table is occupied).</param>
public record TableDto(Guid Id, Guid? AreaId, string Number, int Seats, bool IsActive, Guid? OpenOrderId, string? OpenOrderNumber);
public record SaveTableRequest(Guid? AreaId, string Number, int Seats, bool IsActive = true);
