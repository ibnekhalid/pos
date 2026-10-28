using Restaurant.Domain.Common;

namespace Restaurant.Domain.Entities;

public class DiningArea : BranchEntity
{
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}

/// <summary>A table. "Occupied" is not stored: it is the table's open dine-in order.</summary>
public class RestaurantTable : BranchEntity
{
    public Guid? AreaId { get; set; }
    public string Number { get; set; } = string.Empty;
    public int Seats { get; set; } = 4;
    public bool IsActive { get; set; } = true;
}
