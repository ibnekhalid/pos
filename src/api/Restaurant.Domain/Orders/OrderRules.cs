using Restaurant.Contracts.Enums;

namespace Restaurant.Domain.Orders;

/// <summary>How an order's status follows its kitchen tickets and payments (PLAN.md section 8).</summary>
public static class OrderRules
{
    /// <summary>
    /// The service status from the tickets of the lines sent so far (change notices and cancelled tickets
    /// don't count): none sent → Confirmed; all served → Served (Completed once paid); any preparing →
    /// Preparing; all ready or served → Ready; otherwise SentToKitchen.
    /// </summary>
    public static OrderStatus ServiceStatus(IReadOnlyCollection<KitchenTicketStatus> tickets, bool hasUnsentLines, bool paid)
    {
        if (tickets.Count == 0)
        {
            return OrderStatus.Confirmed;
        }

        OrderStatus status;
        if (tickets.All(t => t == KitchenTicketStatus.Served))
        {
            status = hasUnsentLines ? OrderStatus.Confirmed : OrderStatus.Served;
        }
        else if (tickets.Any(t => t == KitchenTicketStatus.Preparing))
        {
            status = OrderStatus.Preparing;
        }
        else if (tickets.All(t => t is KitchenTicketStatus.Ready or KitchenTicketStatus.Served))
        {
            status = OrderStatus.Ready;
        }
        else
        {
            status = OrderStatus.SentToKitchen;
        }

        return status == OrderStatus.Served && paid ? OrderStatus.Completed : status;
    }

    public static PaymentStatus PaymentStatusFor(decimal total, decimal paid, decimal refunded)
    {
        if (refunded > 0)
        {
            return refunded >= paid ? PaymentStatus.Refunded : PaymentStatus.PartiallyRefunded;
        }
        if (paid <= 0) return PaymentStatus.Unpaid;
        return paid >= total ? PaymentStatus.Paid : PaymentStatus.PartiallyPaid;
    }

    /// <summary>Kitchen ticket moves: New → Preparing → Ready → Served (or straight on); never backwards.</summary>
    public static bool CanMoveTicket(KitchenTicketStatus from, KitchenTicketStatus to) =>
        from is not (KitchenTicketStatus.Served or KitchenTicketStatus.Cancelled)
        && to is not KitchenTicketStatus.Cancelled
        && to > from;
}
