namespace Restaurant.Contracts.Live;

/// <summary>
/// SignalR hub /hubs/live. Messages are hints only: clients re-read state from the API, so a missed
/// message or a reconnect never loses anything (PLAN.md section 3).
/// </summary>
public static class LiveEvents
{
    public const string HubPath = "hubs/live";

    /// <summary>Client → server: receive this branch's hints (checked against the caller's branches).</summary>
    public const string JoinBranch = "JoinBranch";

    /// <summary>Server → client, argument: branch id.</summary>
    public const string TicketsChanged = "TicketsChanged";

    /// <summary>Server → client, argument: branch id.</summary>
    public const string OrdersChanged = "OrdersChanged";
}
