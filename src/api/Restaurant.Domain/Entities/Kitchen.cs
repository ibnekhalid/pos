using Restaurant.Contracts.Enums;
using Restaurant.Domain.Common;

namespace Restaurant.Domain.Entities;

/// <summary>
/// A durable kitchen ticket: one per station per "send". The kitchen screen reads these, so a refresh or
/// reconnect shows exactly the same state (PLAN.md section 8, T8).
/// </summary>
public class KitchenTicket : BranchEntity
{
    public Guid OrderId { get; set; }
    public Guid? StationId { get; set; }
    public int Round { get; set; }
    public KitchenTicketStatus Status { get; set; } = KitchenTicketStatus.New;

    /// <summary>A change notice (items cancelled after the kitchen had them).</summary>
    public bool IsChange { get; set; }

    public string OrderNumber { get; set; } = string.Empty;
    public OrderType OrderType { get; set; }
    public string? TableLabel { get; set; }
    public DateTimeOffset SentAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? ReadyAt { get; set; }
    public DateTimeOffset? ServedAt { get; set; }

    public List<KitchenTicketItem> Items { get; set; } = [];
}

public class KitchenTicketItem : TenantEntity
{
    public Guid TicketId { get; set; }
    public Guid OrderLineId { get; set; }
    public int Quantity { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ModifiersText { get; set; }
    public string? Notes { get; set; }
    public bool Cancelled { get; set; }
}
