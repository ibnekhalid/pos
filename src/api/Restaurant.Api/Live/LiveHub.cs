using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Restaurant.Api.Auth;
using Restaurant.Application.Common.Interfaces;
using Restaurant.Application.Common.Security;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Live;

namespace Restaurant.Api.Live;

/// <summary>
/// Live hints for the POS, orders and kitchen screens. A connection joins a branch only after the same
/// membership and branch checks as an API call (hub calls don't pass through the tenant middleware).
/// </summary>
[Authorize]
public class LiveHub(MembershipAccessService access) : Hub
{
    public async Task JoinBranch(Guid branchId)
    {
        var user = Context.User;
        if (!Guid.TryParse(user?.FindFirst(AppClaims.TenantId)?.Value, out var tenantId)
            || !Guid.TryParse(user.FindFirst(AppClaims.MembershipId)?.Value, out var membershipId)
            || !Guid.TryParse(user.FindFirst(AppClaims.UserId)?.Value, out var userId))
        {
            throw new HubException("Select a restaurant first.");
        }

        var membership = await access.GetAsync(tenantId, membershipId);
        if (membership is null || membership.UserId != userId || membership.Status != MembershipStatus.Active
            || membership.TenantStatus != TenantStatus.Active || !membership.AllowedBranchIds.Contains(branchId))
        {
            throw new HubException("You don't have access to this branch.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, Group(branchId));
    }

    public static string Group(Guid branchId) => $"branch:{branchId}";
}

public class SignalRNotifier(IHubContext<LiveHub> hub, ILogger<SignalRNotifier> logger) : IRealtimeNotifier
{
    public Task OrdersChangedAsync(Guid branchId) => SendAsync(LiveEvents.OrdersChanged, branchId);

    public Task TicketsChangedAsync(Guid branchId) => SendAsync(LiveEvents.TicketsChanged, branchId);

    private async Task SendAsync(string message, Guid branchId)
    {
        try
        {
            await hub.Clients.Group(LiveHub.Group(branchId)).SendAsync(message, branchId);
        }
        catch (Exception ex)
        {
            // A hint that doesn't arrive is harmless: screens re-read on reconnect and on their own refresh.
            logger.LogWarning(ex, "Could not send {Message} for branch {BranchId}", message, branchId);
        }
    }
}
