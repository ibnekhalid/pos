using Restaurant.Application.Common.Interfaces;
using Restaurant.Contracts.Auth;

namespace Restaurant.Application.Common.Security;

/// <summary>The per-request ITenantContext. Only the tenant-resolution middleware (and tests) set it.</summary>
public sealed class TenantContext : ITenantContext
{
    private static readonly IReadOnlySet<string> AllPermissions = Restaurant.Contracts.Auth.Permissions.All.ToHashSet();

    public Guid? UserId { get; private set; }
    public Guid? BranchId { get; private set; }
    public string? RemoteIp { get; private set; }
    public MembershipAccess? Access { get; private set; }

    public Guid? TenantId => Access?.TenantId;
    public Guid? MembershipId => Access?.MembershipId;
    public bool HasFullAccess => Access?.FullAccess == true;
    public bool AllBranches => Access is not null && (Access.FullAccess || Access.AllBranches);

    public IReadOnlySet<string> Permissions => Access is null
        ? new HashSet<string>()
        : Access.FullAccess ? AllPermissions : Access.Permissions;

    public IReadOnlySet<Guid> AllowedBranchIds => Access?.AllowedBranchIds ?? new HashSet<Guid>();

    public bool HasPermission(string permission) => Access is not null && (Access.FullAccess || Access.Permissions.Contains(permission));

    public bool CanAccessBranch(Guid branchId) => Access?.AllowedBranchIds.Contains(branchId) == true;

    public void SetCaller(Guid? userId, string? remoteIp)
    {
        UserId = userId;
        RemoteIp = remoteIp;
    }

    public void SetMembership(MembershipAccess access) => Access = access;

    public void SetBranch(Guid branchId) => BranchId = branchId;
}
