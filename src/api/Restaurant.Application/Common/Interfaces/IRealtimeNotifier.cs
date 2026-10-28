namespace Restaurant.Application.Common.Interfaces;

/// <summary>
/// Tells connected screens of a branch that something changed (SignalR in the API). Called after the
/// change is committed; the screens then re-read from the API, so a lost hint loses nothing.
/// </summary>
public interface IRealtimeNotifier
{
    Task OrdersChangedAsync(Guid branchId);
    Task TicketsChangedAsync(Guid branchId);
}
