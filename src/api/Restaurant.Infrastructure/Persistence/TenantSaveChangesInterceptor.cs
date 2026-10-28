using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Restaurant.Application.Common.Exceptions;
using Restaurant.Application.Common.Interfaces;
using Restaurant.Domain.Common;
using Restaurant.Domain.Entities;

namespace Restaurant.Infrastructure.Persistence;

/// <summary>
/// The write-side half of tenant isolation (the query filters are the read side):
/// <list type="bullet">
/// <item>inserts get the current tenant (and branch) when they don't carry one;</item>
/// <item>with a tenant selected, inserting, updating or deleting another tenant's row throws;</item>
/// <item>a row's tenant can never change;</item>
/// <item>CreatedBy / UpdatedAt / UpdatedBy are stamped.</item>
/// </list>
/// Requests without a tenant (sign-up, sign-in, invites, PIN sign-in) set TenantId explicitly.
/// </summary>
public sealed class TenantSaveChangesInterceptor(ITenantContext tenant) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Apply(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            if (entry.Entity is Entity entity)
            {
                if (entry.State == EntityState.Added)
                {
                    entity.CreatedBy ??= tenant.UserId;
                }
                else if (entry.State == EntityState.Modified)
                {
                    entity.UpdatedAt = now;
                    entity.UpdatedBy = tenant.UserId;
                }
            }

            switch (entry.Entity)
            {
                case ITenantOwned owned:
                    GuardTenant(entry, owned);
                    break;
                case AuditLog log when entry.State == EntityState.Added:
                    log.TenantId ??= tenant.TenantId;
                    break;
            }

            if (entry.State == EntityState.Added && entry.Entity is IBranchOwned branchOwned
                && branchOwned.BranchId == Guid.Empty && tenant.BranchId is { } branchId)
            {
                branchOwned.BranchId = branchId;
            }
        }
    }

    private void GuardTenant(EntityEntry entry, ITenantOwned owned)
    {
        var current = tenant.TenantId;
        var typeName = entry.Metadata.ClrType.Name;

        if (entry.State == EntityState.Added)
        {
            if (owned.TenantId == Guid.Empty)
            {
                owned.TenantId = current ?? throw new TenantIsolationException($"A {typeName} can't be saved without a restaurant.");
            }
            else if (current is not null && owned.TenantId != current)
            {
                throw new TenantIsolationException($"Attempted to create a {typeName} in another restaurant.");
            }
            return;
        }

        var original = (Guid)entry.Property(nameof(ITenantOwned.TenantId)).OriginalValue!;
        if (owned.TenantId != original)
        {
            throw new TenantIsolationException($"A {typeName} can't move to another restaurant.");
        }
        if (current is not null && original != current)
        {
            throw new TenantIsolationException($"Attempted to change a {typeName} of another restaurant.");
        }
    }
}
