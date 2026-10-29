using Restaurant.Contracts.Enums;

namespace Restaurant.Contracts.Kitchen;

/// <summary>What the kitchen needs — never prices or customer details.</summary>
public record KitchenTicketItemDto(Guid Id, int Quantity, string Name, IReadOnlyList<string> Modifiers, string? Notes, bool Cancelled);

/// <param name="IsChange">A change notice (items cancelled after cooking started) to acknowledge.</param>
/// <param name="Printed">A paper copy has been printed somewhere (the counter PC that prints every kitchen ticket skips it).</param>
public record KitchenTicketDto(Guid Id, Guid OrderId, string OrderNumber, OrderType OrderType, string? TableLabel, Guid? StationId, int Round,
    KitchenTicketStatus Status, bool IsChange, DateTimeOffset SentAt, DateTimeOffset? StartedAt, DateTimeOffset? ReadyAt,
    IReadOnlyList<KitchenTicketItemDto> Items, string? OrderNotes, bool Printed = false);

public record UpdateTicketStatusRequest(KitchenTicketStatus Status);
