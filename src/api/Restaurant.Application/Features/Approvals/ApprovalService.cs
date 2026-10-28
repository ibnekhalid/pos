using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Restaurant.Application.Common.Exceptions;
using Restaurant.Application.Common.Interfaces;
using Restaurant.Application.Common.Models;
using Restaurant.Application.Common.Security;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Orders;
using Restaurant.Domain.Entities;

namespace Restaurant.Application.Features.Approvals;

/// <summary>
/// "Manager approval": a cashier without a permission (void, refund, discount) asks a manager to enter
/// their PIN on the same screen. The approver must hold the permission and use this branch; wrong PINs
/// count toward the same lockout as PIN sign-in (PLAN.md section 5).
/// </summary>
public class ApprovalService(
    IApplicationDbContext db,
    ITenantContext tenant,
    MembershipAccessService access,
    IPasswordHasher<AppUser> pinHasher,
    IOptions<AuthSettings> settings,
    TimeProvider clock)
{
    /// <summary>Null when the caller holds the permission; otherwise the approving manager's user id.</summary>
    public async Task<Guid?> RequireAsync(string permission, Approval? approval, Guid branchId, CancellationToken cancellationToken)
    {
        if (tenant.HasPermission(permission))
        {
            return null;
        }
        if (approval is null)
        {
            throw new ForbiddenAccessException("This needs a manager's approval.");
        }

        var tenantId = tenant.RequireTenantId();
        var membership = await db.Memberships.Include(m => m.User)
            .FirstOrDefaultAsync(m => m.Id == approval.MembershipId && m.Status == MembershipStatus.Active && m.PinHash != null, cancellationToken)
            ?? throw new ForbiddenAccessException("That approval isn't valid.");

        var approver = await access.GetAsync(tenantId, membership.Id, cancellationToken);
        if (approver is null || !(approver.FullAccess || approver.Permissions.Contains(permission)) || !approver.AllowedBranchIds.Contains(branchId))
        {
            throw new ForbiddenAccessException("That person can't approve this.");
        }

        var now = clock.GetUtcNow();
        if (membership.PinLockedUntil > now)
        {
            throw new ForbiddenAccessException("Too many wrong PINs. Try again in a few minutes.");
        }
        if (pinHasher.VerifyHashedPassword(membership.User!, membership.PinHash!, approval.Pin) == PasswordVerificationResult.Failed)
        {
            membership.PinFailedCount++;
            if (membership.PinFailedCount >= settings.Value.PinMaxAttempts)
            {
                membership.PinLockedUntil = now.AddMinutes(settings.Value.PinLockMinutes);
                membership.PinFailedCount = 0;
            }
            await db.SaveChangesAsync(cancellationToken);
            throw new ForbiddenAccessException("Wrong approval PIN.");
        }

        membership.PinFailedCount = 0;
        return membership.UserId;
    }

    /// <summary>Staff who could approve <paramref name="permission"/> at the current branch (names only).</summary>
    public async Task<IReadOnlyList<ApproverDto>> ApproversAsync(string permission, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var branchId = tenant.RequireBranchId();
        var candidates = await db.Memberships
            .Where(m => m.Status == MembershipStatus.Active && m.PinHash != null && m.Id != tenant.MembershipId)
            .OrderBy(m => m.DisplayName)
            .Select(m => new { m.Id, m.DisplayName })
            .ToListAsync(cancellationToken);

        var result = new List<ApproverDto>();
        foreach (var candidate in candidates)
        {
            var a = await access.GetAsync(tenantId, candidate.Id, cancellationToken);
            if (a is not null && (a.FullAccess || a.Permissions.Contains(permission)) && a.AllowedBranchIds.Contains(branchId))
            {
                result.Add(new ApproverDto(candidate.Id, candidate.DisplayName));
            }
        }
        return result;
    }
}
