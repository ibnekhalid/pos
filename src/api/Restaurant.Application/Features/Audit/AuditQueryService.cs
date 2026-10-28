using Microsoft.EntityFrameworkCore;
using Restaurant.Application.Common.Interfaces;
using Restaurant.Contracts.Audit;
using Restaurant.Contracts.Common;

namespace Restaurant.Application.Features.Audit;

public class AuditQueryService(IApplicationDbContext db, ITenantContext tenant)
{
    public async Task<PagedResult<AuditLogDto>> ListAsync(
        string? action, string? entityType, DateTimeOffset? from, DateTimeOffset? to, int page, int pageSize,
        CancellationToken cancellationToken = default)
    {
        tenant.RequireTenantId();
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = db.AuditLogs.AsQueryable();
        if (!string.IsNullOrWhiteSpace(action)) query = query.Where(a => a.Action == action);
        if (!string.IsNullOrWhiteSpace(entityType)) query = query.Where(a => a.EntityType == entityType);
        if (from is not null) query = query.Where(a => a.CreatedAt >= from);
        if (to is not null) query = query.Where(a => a.CreatedAt < to);

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(a => a.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new AuditLogDto(
                a.Id,
                a.CreatedAt,
                a.ActorUserId,
                db.Memberships.Where(m => m.UserId == a.ActorUserId).Select(m => m.DisplayName).FirstOrDefault(),
                a.ApproverUserId,
                a.Action,
                a.EntityType,
                a.EntityId,
                a.Reason,
                a.BeforeJson,
                a.AfterJson,
                a.BranchId))
            .ToListAsync(cancellationToken);

        return new PagedResult<AuditLogDto>(items, page, pageSize, total);
    }
}
