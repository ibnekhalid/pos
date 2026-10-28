using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Restaurant.Api.Auth;
using Restaurant.Application.Features.Staff;
using Restaurant.Contracts.Auth;
using Restaurant.Contracts.Staff;

namespace Restaurant.Api.Controllers;

[ApiController]
[Route("api")]
[Authorize]
public class StaffController(StaffService staff, RoleService roles) : ControllerBase
{
    [HttpGet("staff")]
    [RequirePermission(Permissions.Staff.View)]
    public Task<IReadOnlyList<StaffMemberDto>> List(CancellationToken ct) => staff.ListAsync(ct);

    [HttpGet("staff/{membershipId:guid}")]
    [RequirePermission(Permissions.Staff.View)]
    public Task<StaffMemberDto> Get(Guid membershipId, CancellationToken ct) => staff.GetAsync(membershipId, ct);

    /// <summary>Adds a staff member. The invite code in the result is shown once.</summary>
    [HttpPost("staff")]
    [RequirePermission(Permissions.Staff.Manage)]
    public Task<StaffInviteDto> Create(CreateStaffRequest request, CancellationToken ct) => staff.CreateAsync(request, ct);

    [HttpPut("staff/{membershipId:guid}")]
    [RequirePermission(Permissions.Staff.Manage)]
    public Task<StaffMemberDto> Update(Guid membershipId, UpdateStaffRequest request, CancellationToken ct) => staff.UpdateAsync(membershipId, request, ct);

    /// <summary>A new invite code (also how an owner resets a staff member's password).</summary>
    [HttpPost("staff/{membershipId:guid}/invite")]
    [RequirePermission(Permissions.Staff.Manage)]
    public Task<StaffInviteDto> ReissueInvite(Guid membershipId, CancellationToken ct) => staff.ReissueInviteAsync(membershipId, ct);

    /// <summary>Makes this staff member an owner; optionally the caller steps down to Manager.</summary>
    [HttpPost("staff/{membershipId:guid}/transfer-ownership")]
    [RequirePermission(Permissions.Tenant.TransferOwnership)]
    public Task<StaffMemberDto> TransferOwnership(Guid membershipId, TransferOwnershipRequest request, CancellationToken ct) =>
        staff.TransferOwnershipAsync(membershipId, request, ct);

    [HttpGet("permissions")]
    [RequirePermission(Permissions.Staff.View)]
    public IReadOnlyList<PermissionDto> PermissionCatalog() => RoleService.Catalog();

    [HttpGet("roles")]
    [RequirePermission(Permissions.Staff.View)]
    public Task<IReadOnlyList<RoleDto>> ListRoles(CancellationToken ct) => roles.ListAsync(ct);

    [HttpPost("roles")]
    [RequirePermission(Permissions.Staff.ManageRoles)]
    public Task<RoleDto> CreateRole(SaveRoleRequest request, CancellationToken ct) => roles.CreateAsync(request, ct);

    [HttpPut("roles/{roleId:guid}")]
    [RequirePermission(Permissions.Staff.ManageRoles)]
    public Task<RoleDto> UpdateRole(Guid roleId, SaveRoleRequest request, CancellationToken ct) => roles.UpdateAsync(roleId, request, ct);

    [HttpDelete("roles/{roleId:guid}")]
    [RequirePermission(Permissions.Staff.ManageRoles)]
    public async Task<IActionResult> DeleteRole(Guid roleId, CancellationToken ct)
    {
        await roles.DeleteAsync(roleId, ct);
        return NoContent();
    }
}
