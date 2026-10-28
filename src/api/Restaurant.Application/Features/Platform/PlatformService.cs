using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Restaurant.Application.Common.Exceptions;
using Restaurant.Application.Common.Interfaces;
using Restaurant.Application.Common.Security;
using Restaurant.Application.Common.Services;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Tenancy;
using Restaurant.Domain.Constants;
using Restaurant.Domain.Entities;

namespace Restaurant.Application.Features.Platform;

/// <summary>
/// Platform administration: separate from restaurant ownership (PLAN.md section 4). Checked against
/// the Identity role on every call, never against a token claim. A platform admin sees tenants'
/// names and counts, not their business data.
/// </summary>
public class PlatformService(
    IApplicationDbContext db,
    UserManager<AppUser> users,
    ITenantContext tenant,
    MembershipAccessService access,
    AuditLogger audit)
{
    public async Task<IReadOnlyList<PlatformTenantDto>> ListTenantsAsync(CancellationToken cancellationToken = default)
    {
        await EnsurePlatformAdminAsync();

        // Counts only: IgnoreQueryFilters applies to a whole query, so each is its own query.
        var branches = await db.Branches.IgnoreQueryFilters()
            .GroupBy(b => b.TenantId)
            .Select(g => new { TenantId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TenantId, x => x.Count, cancellationToken);
        var members = await db.Memberships.IgnoreQueryFilters()
            .Where(m => m.Status == MembershipStatus.Active)
            .GroupBy(m => m.TenantId)
            .Select(g => new { TenantId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TenantId, x => x.Count, cancellationToken);

        var tenants = await db.Tenants.OrderBy(t => t.Name).ToListAsync(cancellationToken);
        return tenants
            .Select(t => new PlatformTenantDto(t.Id, t.Name, t.Status, t.CreatedAt, branches.GetValueOrDefault(t.Id), members.GetValueOrDefault(t.Id)))
            .ToList();
    }

    public async Task SetStatusAsync(Guid tenantId, TenantStatus status, CancellationToken cancellationToken = default)
    {
        await EnsurePlatformAdminAsync();
        var entity = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken) ?? throw new NotFoundException("Restaurant");
        if (entity.Status == status)
        {
            return;
        }

        entity.Status = status;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Record(status == TenantStatus.Suspended ? AuditActions.TenantSuspended : AuditActions.TenantActivated,
            nameof(Tenant), entity.Id, tenantId: entity.Id);
        await db.SaveChangesAsync(cancellationToken);
        access.InvalidateTenant(entity.Id);
    }

    private async Task EnsurePlatformAdminAsync()
    {
        var user = await users.FindByIdAsync(tenant.RequireUserId().ToString());
        if (user is null || !await users.IsInRoleAsync(user, SystemRoleKeys.PlatformAdminRole))
        {
            throw new ForbiddenAccessException();
        }
    }
}
