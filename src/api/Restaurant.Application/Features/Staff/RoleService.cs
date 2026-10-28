using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Restaurant.Application.Common.Exceptions;
using Restaurant.Application.Common.Extensions;
using Restaurant.Application.Common.Interfaces;
using Restaurant.Application.Common.Security;
using Restaurant.Application.Common.Services;
using Restaurant.Contracts.Auth;
using Restaurant.Contracts.Staff;
using Restaurant.Domain.Entities;

namespace Restaurant.Application.Features.Staff;

public class SaveRoleRequestValidator : AbstractValidator<SaveRoleRequest>
{
    public SaveRoleRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(60);
        RuleFor(x => x.Description).MaximumLength(300);
        RuleFor(x => x.Permissions).NotNull();
        RuleForEach(x => x.Permissions).Must(p => Permissions.All.Contains(p)).WithMessage("Unknown permission '{PropertyValue}'.");
    }
}

/// <summary>
/// Tenant roles. Nobody can create or edit a role with permissions they don't hold themselves, and
/// the Owner role can't be changed (PLAN.md section 5).
/// </summary>
public class RoleService(
    IApplicationDbContext db,
    ITenantContext tenant,
    MembershipAccessService access,
    AuditLogger audit,
    IValidator<SaveRoleRequest> validator)
{
    public static IReadOnlyList<PermissionDto> Catalog() =>
        Permissions.All.Select(p => new PermissionDto(p, p[..p.IndexOf('.')])).ToList();

    public async Task<IReadOnlyList<RoleDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        tenant.RequireTenantId();
        var roles = await db.TenantRoles.Include(r => r.Permissions)
            .OrderByDescending(r => r.IsSystem).ThenBy(r => r.Name)
            .ToListAsync(cancellationToken);
        return roles.Select(ToDto).ToList();
    }

    public async Task<RoleDto> CreateAsync(SaveRoleRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = tenant.RequireTenantId();
        await ValidateAsync(request, null, cancellationToken);

        var role = new TenantRole { TenantId = tenantId, Name = request.Name.Trim(), Description = request.Description?.Trim() };
        role.Permissions = request.Permissions.Distinct()
            .Select(p => new RolePermission { TenantId = tenantId, RoleId = role.Id, Permission = p })
            .ToList();
        db.TenantRoles.Add(role);

        audit.Record(AuditActions.RoleCreated, nameof(TenantRole), role.Id, after: ToDto(role));
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(role);
    }

    public async Task<RoleDto> UpdateAsync(Guid id, SaveRoleRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = tenant.RequireTenantId();
        var role = await db.TenantRoles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new NotFoundException("Role");
        if (role.FullAccess)
        {
            throw new ForbiddenAccessException("The Owner role can't be changed.");
        }
        await ValidateAsync(request, id, cancellationToken);

        // Taking permissions away is also limited to ones the caller holds: otherwise a manager could
        // strip a permission they can't grant back.
        EnsureCallerHolds(role.Permissions.Select(p => p.Permission).Except(request.Permissions));

        var before = ToDto(role);
        role.Name = request.Name.Trim();
        role.Description = request.Description?.Trim();
        var wanted = request.Permissions.Distinct().ToList();
        foreach (var link in role.Permissions.Where(p => !wanted.Contains(p.Permission)).ToList())
        {
            role.Permissions.Remove(link);
            db.RolePermissions.Remove(link);
        }
        foreach (var permission in wanted.Where(p => role.Permissions.All(x => x.Permission != p)))
        {
            role.Permissions.Add(new RolePermission { TenantId = tenantId, RoleId = role.Id, Permission = permission });
        }
        role.UpdatedAt = DateTimeOffset.UtcNow;

        audit.Record(AuditActions.RoleUpdated, nameof(TenantRole), role.Id, before, ToDto(role));
        await db.SaveChangesAsync(cancellationToken);
        access.InvalidateTenant(tenantId);
        return ToDto(role);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        tenant.RequireTenantId();
        var role = await db.TenantRoles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new NotFoundException("Role");
        if (role.IsSystem)
        {
            throw new ConflictException("Built-in roles can't be deleted.");
        }
        if (await db.MembershipRoles.AnyAsync(mr => mr.RoleId == id, cancellationToken))
        {
            throw new ConflictException("This role is assigned to staff. Remove it from them first.");
        }
        EnsureCallerHolds(role.Permissions.Select(p => p.Permission));

        audit.Record(AuditActions.RoleDeleted, nameof(TenantRole), role.Id, before: ToDto(role));
        db.TenantRoles.Remove(role);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ValidateAsync(SaveRoleRequest request, Guid? exceptId, CancellationToken cancellationToken)
    {
        await validator.ValidateAndThrowAppExceptionAsync(request, cancellationToken);
        EnsureCallerHolds(request.Permissions);

        var name = request.Name.Trim();
        if (await db.TenantRoles.AnyAsync(r => r.Name == name && r.Id != exceptId, cancellationToken))
        {
            throw new ConflictException("A role with this name already exists.");
        }
    }

    private void EnsureCallerHolds(IEnumerable<string> permissions)
    {
        if (!tenant.HasFullAccess && permissions.Any(p => !tenant.HasPermission(p)))
        {
            throw new ForbiddenAccessException("You can't grant or remove permissions you don't have yourself.");
        }
    }

    public static RoleDto ToDto(TenantRole r) => new(
        r.Id, r.Name, r.Description, r.IsSystem, r.FullAccess,
        r.FullAccess ? Permissions.All : r.Permissions.Select(p => p.Permission).Order().ToList());
}
