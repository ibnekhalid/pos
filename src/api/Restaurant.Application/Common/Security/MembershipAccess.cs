using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Restaurant.Application.Common.Interfaces;
using Restaurant.Contracts.Enums;

namespace Restaurant.Application.Common.Security;

/// <summary>What one membership may do right now: status, permissions and branches.</summary>
public sealed record MembershipAccess(
    Guid MembershipId,
    Guid TenantId,
    Guid UserId,
    MembershipStatus Status,
    TenantStatus TenantStatus,
    bool FullAccess,
    bool AllBranches,
    IReadOnlySet<string> Permissions,
    IReadOnlySet<Guid> AllowedBranchIds);

/// <summary>
/// Caches MembershipAccess for up to a minute, so every request doesn't reload roles and branches.
/// Any change to a tenant's staff, roles or branches bumps that tenant's version, which makes all of
/// its cached entries unreachable at once (PLAN.md section 4: revocation takes effect immediately).
/// </summary>
public sealed class MembershipAccessCache(IMemoryCache cache)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<Guid, int> _versions = new();

    public MembershipAccess? Get(Guid tenantId, Guid membershipId) =>
        cache.TryGetValue(Key(tenantId, membershipId), out MembershipAccess? access) ? access : null;

    public void Set(MembershipAccess access) =>
        cache.Set(Key(access.TenantId, access.MembershipId), access, Lifetime);

    public void InvalidateTenant(Guid tenantId) => _versions.AddOrUpdate(tenantId, 1, (_, version) => version + 1);

    private string Key(Guid tenantId, Guid membershipId) =>
        $"rp:access:{tenantId}:{membershipId}:{_versions.GetValueOrDefault(tenantId)}";
}

public sealed class MembershipAccessService(IApplicationDbContext db, MembershipAccessCache cache)
{
    public async Task<MembershipAccess?> GetAsync(Guid tenantId, Guid membershipId, CancellationToken cancellationToken = default)
    {
        var cached = cache.Get(tenantId, membershipId);
        if (cached is not null)
        {
            return cached;
        }

        // Cross-tenant by design: this is what decides the tenant, so it scopes the query itself.
        var membership = await db.Memberships.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.Id == membershipId && m.TenantId == tenantId)
            .Select(m => new
            {
                m.Id,
                m.TenantId,
                m.UserId,
                m.Status,
                m.AllBranches,
                Roles = m.Roles.Select(r => new { r.Role!.FullAccess, Permissions = r.Role.Permissions.Select(p => p.Permission).ToList() }).ToList(),
                BranchIds = m.Branches.Select(b => b.BranchId).ToList()
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (membership is null)
        {
            return null;
        }

        var tenantStatus = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => (TenantStatus?)t.Status)
            .FirstOrDefaultAsync(cancellationToken);

        if (tenantStatus is null)
        {
            return null;
        }

        var tenantBranchIds = await db.Branches.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.TenantId == tenantId)
            .Select(b => b.Id)
            .ToListAsync(cancellationToken);

        var fullAccess = membership.Roles.Any(r => r.FullAccess);
        var allBranches = fullAccess || membership.AllBranches;
        var allowedBranches = allBranches
            ? tenantBranchIds.ToHashSet()
            : membership.BranchIds.Intersect(tenantBranchIds).ToHashSet();

        var access = new MembershipAccess(
            membership.Id,
            membership.TenantId,
            membership.UserId,
            membership.Status,
            tenantStatus.Value,
            fullAccess,
            membership.AllBranches,
            membership.Roles.SelectMany(r => r.Permissions).ToHashSet(),
            allowedBranches);

        cache.Set(access);
        return access;
    }

    public void InvalidateTenant(Guid tenantId) => cache.InvalidateTenant(tenantId);
}
