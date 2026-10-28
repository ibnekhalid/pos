using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Restaurant.Application.Common.Exceptions;
using Restaurant.Application.Common.Extensions;
using Restaurant.Application.Common.Interfaces;
using Restaurant.Application.Common.Models;
using Restaurant.Application.Common.Security;
using Restaurant.Application.Common.Services;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Staff;
using Restaurant.Domain.Constants;
using Restaurant.Domain.Entities;

namespace Restaurant.Application.Features.Staff;

public class CreateStaffRequestValidator : AbstractValidator<CreateStaffRequest>
{
    public CreateStaffRequestValidator()
    {
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Email).EmailAddress().MaximumLength(200).When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Phone).MaximumLength(30);
        RuleFor(x => x).Must(x => !string.IsNullOrWhiteSpace(x.Email) || !string.IsNullOrWhiteSpace(x.Phone))
            .WithName("Email").WithMessage("Enter an email address or a mobile number to sign in with.");
        RuleFor(x => x.RoleIds).NotEmpty().WithMessage("Choose at least one role.");
        RuleFor(x => x.BranchIds).NotEmpty().When(x => !x.AllBranches).WithMessage("Choose at least one branch.");
    }
}

public class UpdateStaffRequestValidator : AbstractValidator<UpdateStaffRequest>
{
    public UpdateStaffRequestValidator()
    {
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.RoleIds).NotEmpty().WithMessage("Choose at least one role.");
        RuleFor(x => x.BranchIds).NotEmpty().When(x => !x.AllBranches).WithMessage("Choose at least one branch.");
    }
}

/// <summary>
/// Staff memberships: invite, change roles and branches, disable. Callers can never hand out more than
/// they have (permissions or branches), only an owner can change an owner, and the restaurant always
/// keeps at least one active owner.
/// </summary>
public class StaffService(
    IApplicationDbContext db,
    UserManager<AppUser> users,
    ITenantContext tenant,
    MembershipAccessService access,
    AuditLogger audit,
    IOptions<AuthSettings> settings,
    IValidator<CreateStaffRequest> createValidator,
    IValidator<UpdateStaffRequest> updateValidator)
{
    public async Task<IReadOnlyList<StaffMemberDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        tenant.RequireTenantId();
        var members = await MembersQuery().OrderBy(m => m.DisplayName).ToListAsync(cancellationToken);
        return members.Select(ToDto).ToList();
    }

    public async Task<StaffMemberDto> GetAsync(Guid membershipId, CancellationToken cancellationToken = default)
    {
        tenant.RequireTenantId();
        return ToDto(await FindAsync(membershipId, cancellationToken));
    }

    public async Task<StaffInviteDto> CreateAsync(CreateStaffRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = tenant.RequireTenantId();
        await createValidator.ValidateAndThrowAppExceptionAsync(request, cancellationToken);

        var roles = await LoadGrantableRolesAsync(request.RoleIds, cancellationToken);
        var branchIds = await ValidateBranchesAsync(request.AllBranches, request.BranchIds, cancellationToken);

        var email = string.IsNullOrWhiteSpace(request.Email) ? null : LoginNames.NormalizeEmail(request.Email);
        var phone = string.IsNullOrWhiteSpace(request.Phone) ? null : LoginNames.NormalizePhone(request.Phone);

        var user = (email is not null ? await users.FindByEmailAsync(email) : null)
            ?? (phone is not null ? await db.Users.FirstOrDefaultAsync(u => u.PhoneNumber == phone, cancellationToken) : null);

        if (user is not null && await db.Memberships.AnyAsync(m => m.UserId == user.Id, cancellationToken))
        {
            throw new ConflictException("This person is already on your staff.");
        }

        if (user is null)
        {
            if (phone is not null && await db.Users.AnyAsync(u => u.PhoneNumber == phone, cancellationToken))
            {
                throw new ConflictException("This mobile number belongs to a different account.");
            }

            user = new AppUser { UserName = email ?? phone, Email = email, PhoneNumber = phone, FullName = request.DisplayName.Trim() };
            var created = await users.CreateAsync(user);
            if (!created.Succeeded)
            {
                throw new ValidationAppException(created.Errors.Select(e => new FluentValidation.Results.ValidationFailure("Email", e.Description)));
            }
        }

        var membership = new StaffMembership
        {
            TenantId = tenantId,
            UserId = user.Id,
            User = user,
            DisplayName = request.DisplayName.Trim(),
            Status = MembershipStatus.Invited,
            AllBranches = request.AllBranches
        };
        membership.Roles = roles.Select(r => new MembershipRole { TenantId = tenantId, MembershipId = membership.Id, RoleId = r.Id, Role = r }).ToList();
        membership.Branches = branchIds.Select(b => new MembershipBranch { TenantId = tenantId, MembershipId = membership.Id, BranchId = b }).ToList();
        var code = IssueInvite(membership);
        db.Memberships.Add(membership);

        audit.Record(AuditActions.StaffCreated, nameof(StaffMembership), membership.Id, after: ToDto(membership));
        await db.SaveChangesAsync(cancellationToken);
        return new StaffInviteDto(ToDto(membership), code, membership.InviteExpiresAt!.Value);
    }

    public async Task<StaffMemberDto> UpdateAsync(Guid membershipId, UpdateStaffRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = tenant.RequireTenantId();
        await updateValidator.ValidateAndThrowAppExceptionAsync(request, cancellationToken);

        var membership = await FindAsync(membershipId, cancellationToken);
        EnsureCanManage(membership);

        if (membership.Id == tenant.MembershipId && request.IsDisabled)
        {
            throw new ConflictException("You can't disable your own access.");
        }

        var roles = await LoadGrantableRolesAsync(request.RoleIds, cancellationToken);
        var branchIds = await ValidateBranchesAsync(request.AllBranches, request.BranchIds, cancellationToken);

        // Roles being removed are limited the same way as roles being granted.
        var removedRoleIds = membership.Roles.Select(r => r.RoleId).Except(request.RoleIds).ToList();
        if (removedRoleIds.Count > 0)
        {
            await LoadGrantableRolesAsync(removedRoleIds, cancellationToken);
        }

        var before = ToDto(membership);
        var willHaveFullAccess = roles.Any(r => r.FullAccess) && !request.IsDisabled;
        if (membership.Roles.Any(r => r.Role!.FullAccess) && !willHaveFullAccess)
        {
            var otherOwners = await db.Memberships.AnyAsync(m =>
                m.Id != membership.Id && m.Status == MembershipStatus.Active && m.Roles.Any(r => r.Role!.FullAccess), cancellationToken);
            if (!otherOwners)
            {
                throw new ConflictException("The restaurant needs at least one active owner.");
            }
        }

        membership.DisplayName = request.DisplayName.Trim();
        membership.AllBranches = request.AllBranches;

        // Add and remove only what changed: the links are keyed by (membership, role/branch).
        foreach (var link in membership.Roles.Where(r => roles.All(x => x.Id != r.RoleId)).ToList())
        {
            membership.Roles.Remove(link);
            db.MembershipRoles.Remove(link);
        }
        foreach (var role in roles.Where(r => membership.Roles.All(x => x.RoleId != r.Id)))
        {
            membership.Roles.Add(new MembershipRole { TenantId = tenantId, MembershipId = membership.Id, RoleId = role.Id, Role = role });
        }
        foreach (var link in membership.Branches.Where(b => !branchIds.Contains(b.BranchId)).ToList())
        {
            membership.Branches.Remove(link);
            db.MembershipBranches.Remove(link);
        }
        foreach (var branchId in branchIds.Where(id => membership.Branches.All(x => x.BranchId != id)))
        {
            membership.Branches.Add(new MembershipBranch { TenantId = tenantId, MembershipId = membership.Id, BranchId = branchId });
        }

        if (request.IsDisabled && membership.Status != MembershipStatus.Disabled)
        {
            membership.Status = MembershipStatus.Disabled;
            await EndSessionsAsync(membership.Id, cancellationToken);
        }
        else if (!request.IsDisabled && membership.Status == MembershipStatus.Disabled)
        {
            membership.Status = membership.InviteCodeHash is null ? MembershipStatus.Active : MembershipStatus.Invited;
        }
        membership.UpdatedAt = DateTimeOffset.UtcNow;

        audit.Record(AuditActions.StaffUpdated, nameof(StaffMembership), membership.Id, before, ToDto(membership));
        await db.SaveChangesAsync(cancellationToken);
        access.InvalidateTenant(tenantId);
        return ToDto(membership);
    }

    /// <summary>
    /// A fresh invite code. For someone who only works here it also lets them choose a new password
    /// (the owner-assisted reset); someone who also works elsewhere keeps their own password.
    /// </summary>
    public async Task<StaffInviteDto> ReissueInviteAsync(Guid membershipId, CancellationToken cancellationToken = default)
    {
        tenant.RequireTenantId();
        var membership = await FindAsync(membershipId, cancellationToken);
        EnsureCanManage(membership);
        if (membership.Status == MembershipStatus.Disabled)
        {
            throw new ConflictException("Enable this staff member first.");
        }

        var code = IssueInvite(membership);
        audit.Record(AuditActions.StaffInviteIssued, nameof(StaffMembership), membership.Id);
        await db.SaveChangesAsync(cancellationToken);
        return new StaffInviteDto(ToDto(membership), code, membership.InviteExpiresAt!.Value);
    }

    /// <summary>
    /// Hands the restaurant to another active staff member: they become an owner (all branches), and with
    /// StepDown the caller keeps working as a Manager. Confirmed with the caller's password; one transaction,
    /// one audit row.
    /// </summary>
    public async Task<StaffMemberDto> TransferOwnershipAsync(Guid membershipId, TransferOwnershipRequest request, CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        if (!tenant.HasFullAccess)
        {
            throw new ForbiddenAccessException("Only an owner can hand over the restaurant.");
        }
        if (membershipId == tenant.MembershipId)
        {
            throw new ValidationAppException(nameof(membershipId), "Choose someone else.");
        }

        var caller = await users.FindByIdAsync(tenant.RequireUserId().ToString());
        if (caller is null || string.IsNullOrEmpty(request.Password) || !await users.CheckPasswordAsync(caller, request.Password))
        {
            throw new ValidationAppException(nameof(request.Password), "That password isn't right.");
        }

        var target = await FindAsync(membershipId, cancellationToken);
        if (target.Status != MembershipStatus.Active)
        {
            throw new ConflictException("Only someone who has accepted their invite and isn't disabled can become an owner.");
        }
        if (target.Roles.Any(r => r.Role!.FullAccess))
        {
            throw new ConflictException("This person is already an owner.");
        }

        var roles = await db.TenantRoles.Where(r => r.IsSystem && (r.SystemKey == SystemRoleKeys.Owner || r.SystemKey == SystemRoleKeys.Manager))
            .ToListAsync(cancellationToken);
        var ownerRole = roles.Single(r => r.SystemKey == SystemRoleKeys.Owner);
        var before = ToDto(target);

        target.Roles.Add(new MembershipRole { TenantId = tenantId, MembershipId = target.Id, RoleId = ownerRole.Id, Role = ownerRole });
        target.AllBranches = true;
        target.UpdatedAt = DateTimeOffset.UtcNow;

        if (request.StepDown)
        {
            var me = await FindAsync(tenant.RequireMembershipId(), cancellationToken);
            foreach (var link in me.Roles.Where(r => r.Role!.FullAccess).ToList())
            {
                me.Roles.Remove(link);
                db.MembershipRoles.Remove(link);
            }
            if (me.Roles.Count == 0 && roles.FirstOrDefault(r => r.SystemKey == SystemRoleKeys.Manager) is { } manager)
            {
                me.Roles.Add(new MembershipRole { TenantId = tenantId, MembershipId = me.Id, RoleId = manager.Id, Role = manager });
            }
            me.UpdatedAt = DateTimeOffset.UtcNow;
        }

        audit.Record(AuditActions.OwnershipTransferred, nameof(StaffMembership), target.Id, before,
            new { NewOwner = target.DisplayName, PreviousOwnerSteppedDown = request.StepDown });
        await db.SaveChangesAsync(cancellationToken);
        access.InvalidateTenant(tenantId);
        return ToDto(target);
    }

    private string IssueInvite(StaffMembership membership)
    {
        var code = Secrets.NewInviteCode();
        membership.InviteCodeHash = Secrets.HashInviteCode(code);
        membership.InviteExpiresAt = DateTimeOffset.UtcNow.AddDays(settings.Value.InviteDays);
        return code;
    }

    private void EnsureCanManage(StaffMembership membership)
    {
        if (membership.Roles.Any(r => r.Role!.FullAccess) && !tenant.HasFullAccess)
        {
            throw new ForbiddenAccessException("Only an owner can change an owner's access.");
        }
    }

    private async Task<List<TenantRole>> LoadGrantableRolesAsync(IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken)
    {
        var ids = roleIds.Distinct().ToList();
        var roles = await db.TenantRoles.Include(r => r.Permissions).Where(r => ids.Contains(r.Id)).ToListAsync(cancellationToken);
        if (roles.Count != ids.Count)
        {
            throw new NotFoundException("Role");
        }

        if (!tenant.HasFullAccess)
        {
            if (roles.Any(r => r.FullAccess))
            {
                throw new ForbiddenAccessException("Only an owner can grant the Owner role.");
            }
            if (roles.SelectMany(r => r.Permissions).Any(p => !tenant.HasPermission(p.Permission)))
            {
                throw new ForbiddenAccessException("You can't grant permissions you don't have yourself.");
            }
        }
        return roles;
    }

    private async Task<List<Guid>> ValidateBranchesAsync(bool allBranches, IReadOnlyCollection<Guid> branchIds, CancellationToken cancellationToken)
    {
        if (allBranches)
        {
            if (!tenant.AllBranches)
            {
                throw new ForbiddenAccessException("You can't grant access to all branches.");
            }
            return [];
        }

        var ids = branchIds.Distinct().ToList();
        if (await db.Branches.CountAsync(b => ids.Contains(b.Id), cancellationToken) != ids.Count)
        {
            throw new NotFoundException("Branch");
        }
        if (ids.Any(id => !tenant.CanAccessBranch(id)))
        {
            throw new ForbiddenAccessException("You can't grant access to a branch you can't use yourself.");
        }
        return ids;
    }

    private async Task EndSessionsAsync(Guid membershipId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var sessions = await db.RefreshTokens.Where(t => t.MembershipId == membershipId && t.RevokedAt == null).ToListAsync(cancellationToken);
        foreach (var session in sessions)
        {
            session.RevokedAt = now;
        }
    }

    private IQueryable<StaffMembership> MembersQuery() => db.Memberships
        .Include(m => m.User)
        .Include(m => m.Roles).ThenInclude(r => r.Role)
        .Include(m => m.Branches);

    private async Task<StaffMembership> FindAsync(Guid membershipId, CancellationToken cancellationToken) =>
        await MembersQuery().FirstOrDefaultAsync(m => m.Id == membershipId, cancellationToken)
        ?? throw new NotFoundException("Staff member");

    private static StaffMemberDto ToDto(StaffMembership m) => new(
        m.Id,
        m.UserId,
        m.DisplayName,
        m.User?.Email,
        m.User?.PhoneNumber,
        m.Status,
        m.Roles.Any(r => r.Role?.FullAccess == true),
        m.AllBranches,
        m.Branches.Select(b => b.BranchId).ToList(),
        m.Roles.Select(r => r.RoleId).ToList(),
        m.PinHash is not null,
        m.InviteExpiresAt);
}
