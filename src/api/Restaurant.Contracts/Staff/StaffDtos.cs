using Restaurant.Contracts.Enums;

namespace Restaurant.Contracts.Staff;

public record PermissionDto(string Name, string Group);

public record RoleDto(Guid Id, string Name, string? Description, bool IsSystem, bool FullAccess, IReadOnlyList<string> Permissions);

public record SaveRoleRequest(string Name, string? Description, IReadOnlyList<string> Permissions);

public record StaffMemberDto(
    Guid MembershipId,
    Guid UserId,
    string DisplayName,
    string? Email,
    string? Phone,
    MembershipStatus Status,
    bool IsOwner,
    bool AllBranches,
    IReadOnlyList<Guid> BranchIds,
    IReadOnlyList<Guid> RoleIds,
    bool HasPin,
    DateTimeOffset? InviteExpiresAt);

/// <param name="Email">Email or Phone is required; it is what the staff member signs in with.</param>
public record CreateStaffRequest(
    string DisplayName,
    string? Email,
    string? Phone,
    IReadOnlyList<Guid> RoleIds,
    bool AllBranches,
    IReadOnlyList<Guid> BranchIds);

public record UpdateStaffRequest(
    string DisplayName,
    IReadOnlyList<Guid> RoleIds,
    bool AllBranches,
    IReadOnlyList<Guid> BranchIds,
    bool IsDisabled);

/// <param name="InviteCode">Shown once; the owner passes it to the staff member (no email/SMS provider yet).</param>
public record StaffInviteDto(StaffMemberDto Member, string InviteCode, DateTimeOffset ExpiresAt);

/// <summary>Makes a staff member an owner. With StepDown the caller then becomes a Manager (if they hold no other role).</summary>
/// <param name="Password">The caller's own password: handing over a restaurant is confirmed like a sign-in.</param>
public record TransferOwnershipRequest(string Password, bool StepDown);
