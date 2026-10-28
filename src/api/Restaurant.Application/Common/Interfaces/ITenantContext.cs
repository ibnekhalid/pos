using Restaurant.Application.Common.Exceptions;

namespace Restaurant.Application.Common.Interfaces;

/// <summary>
/// Who is calling and for which tenant/branch, resolved once per request from the token plus a
/// membership check (PLAN.md section 4). Services and the DbContext query filters read it; nothing
/// takes a tenant id from the request body.
/// </summary>
public interface ITenantContext
{
    Guid? UserId { get; }
    Guid? TenantId { get; }
    Guid? MembershipId { get; }

    /// <summary>The branch from X-Branch-Id, already checked against the caller's allowed branches.</summary>
    Guid? BranchId { get; }

    string? RemoteIp { get; }

    /// <summary>Holds a role with full access (the Owner role).</summary>
    bool HasFullAccess { get; }

    /// <summary>May use every branch, including ones created later.</summary>
    bool AllBranches { get; }

    /// <summary>The caller's effective permissions (every permission for full access).</summary>
    IReadOnlySet<string> Permissions { get; }

    IReadOnlySet<Guid> AllowedBranchIds { get; }

    bool HasPermission(string permission);
    bool CanAccessBranch(Guid branchId);
}

public static class TenantContextExtensions
{
    public static Guid RequireUserId(this ITenantContext context) =>
        context.UserId ?? throw new AuthenticationFailedException("Sign in first.");

    public static Guid RequireTenantId(this ITenantContext context) =>
        context.TenantId ?? throw new ForbiddenAccessException("Select a restaurant first.");

    public static Guid RequireMembershipId(this ITenantContext context) =>
        context.MembershipId ?? throw new ForbiddenAccessException("Select a restaurant first.");

    public static Guid RequireBranchId(this ITenantContext context) =>
        context.BranchId ?? throw new ValidationAppException("X-Branch-Id", "Select a branch first.");
}
