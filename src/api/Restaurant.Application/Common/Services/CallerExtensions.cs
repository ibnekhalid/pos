using Microsoft.EntityFrameworkCore;
using Restaurant.Application.Common.Exceptions;
using Restaurant.Application.Common.Interfaces;

namespace Restaurant.Application.Common.Services;

public static class CallerExtensions
{
    /// <summary>The caller's name in this restaurant (printed on receipts as the cashier).</summary>
    public static async Task<string?> CallerNameAsync(this IApplicationDbContext db, ITenantContext tenant, CancellationToken cancellationToken) =>
        tenant.MembershipId is { } id
            ? await db.Memberships.Where(m => m.Id == id).Select(m => m.DisplayName).FirstOrDefaultAsync(cancellationToken)
            : null;

    /// <summary>The time zone a branch's business dates and printed times use.</summary>
    public static TimeZoneInfo TimeZone(string id) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : TimeZoneInfo.Utc;

    public static void EnsureBranch(this ITenantContext tenant, Guid branchId)
    {
        if (!tenant.CanAccessBranch(branchId))
        {
            throw new ForbiddenAccessException("You don't have access to this branch.");
        }
    }
}
