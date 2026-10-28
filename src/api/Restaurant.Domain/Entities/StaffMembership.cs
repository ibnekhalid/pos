using Restaurant.Contracts.Enums;
using Restaurant.Domain.Common;

namespace Restaurant.Domain.Entities;

/// <summary>
/// Links a user to one tenant: their roles, which branches they may use, their counter PIN and
/// any pending invite. Access is always checked through an active membership, never the user alone.
/// </summary>
public class StaffMembership : TenantEntity
{
    public Guid UserId { get; set; }
    public AppUser? User { get; set; }

    /// <summary>The name this restaurant knows the person by (receipts, kitchen screen).</summary>
    public string DisplayName { get; set; } = string.Empty;

    public MembershipStatus Status { get; set; } = MembershipStatus.Invited;
    public bool AllBranches { get; set; }

    public string? PinHash { get; set; }
    public int PinFailedCount { get; set; }
    public DateTimeOffset? PinLockedUntil { get; set; }

    public string? InviteCodeHash { get; set; }
    public DateTimeOffset? InviteExpiresAt { get; set; }

    public List<MembershipRole> Roles { get; set; } = [];
    public List<MembershipBranch> Branches { get; set; } = [];
}

public class MembershipRole : ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid MembershipId { get; set; }
    public Guid RoleId { get; set; }
    public TenantRole? Role { get; set; }
}

public class MembershipBranch : ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid MembershipId { get; set; }
    public Guid BranchId { get; set; }
}
