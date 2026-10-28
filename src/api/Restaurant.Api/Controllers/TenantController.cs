using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Restaurant.Api.Auth;
using Restaurant.Application.Common.Interfaces;
using Restaurant.Application.Features.Tenancy;
using Restaurant.Contracts.Auth;
using Restaurant.Contracts.Tenancy;

namespace Restaurant.Api.Controllers;

[ApiController]
[Route("api")]
[Authorize]
public class TenantController(TenancyService tenancy, DeviceService devices, ITenantContext tenant) : ControllerBase
{
    [HttpGet("tenant")]
    public Task<TenantDto> GetTenant(CancellationToken ct) => tenancy.GetTenantAsync(ct);

    [HttpPut("tenant")]
    [RequirePermission(Permissions.Tenant.Manage)]
    public Task<TenantDto> UpdateTenant(UpdateTenantRequest request, CancellationToken ct) => tenancy.UpdateTenantAsync(request, ct);

    /// <summary>The branches the caller may use.</summary>
    [HttpGet("branches")]
    public Task<IReadOnlyList<BranchDto>> ListBranches(CancellationToken ct) => tenancy.ListBranchesAsync(ct);

    /// <summary>The branch selected with X-Branch-Id.</summary>
    [HttpGet("branches/current")]
    public Task<BranchDto> CurrentBranch(CancellationToken ct) => tenancy.GetBranchAsync(tenant.RequireBranchId(), ct);

    [HttpGet("branches/{branchId:guid}")]
    public Task<BranchDto> GetBranch(Guid branchId, CancellationToken ct) => tenancy.GetBranchAsync(branchId, ct);

    [HttpPost("branches")]
    [RequirePermission(Permissions.Tenant.ManageBranches)]
    public Task<BranchDto> CreateBranch(SaveBranchRequest request, CancellationToken ct) => tenancy.CreateBranchAsync(request, ct);

    [HttpPut("branches/{branchId:guid}")]
    [RequirePermission(Permissions.Tenant.ManageBranches)]
    public Task<BranchDto> UpdateBranch(Guid branchId, SaveBranchRequest request, CancellationToken ct) => tenancy.UpdateBranchAsync(branchId, request, ct);

    [HttpPut("branches/{branchId:guid}/settings")]
    [RequirePermission(Permissions.Tenant.ManageSettings)]
    public Task<BranchDto> UpdateBranchSettings(Guid branchId, BranchSettingsDto request, CancellationToken ct) =>
        tenancy.UpdateBranchSettingsAsync(branchId, request, ct);

    [HttpGet("branches/{branchId:guid}/registers")]
    public Task<IReadOnlyList<RegisterDto>> ListRegisters(Guid branchId, CancellationToken ct) => tenancy.ListRegistersAsync(branchId, ct);

    [HttpPost("branches/{branchId:guid}/registers")]
    [RequirePermission(Permissions.Tenant.ManageSettings)]
    public Task<RegisterDto> CreateRegister(Guid branchId, SaveRegisterRequest request, CancellationToken ct) =>
        tenancy.CreateRegisterAsync(branchId, request, ct);

    [HttpPut("branches/{branchId:guid}/registers/{registerId:guid}")]
    [RequirePermission(Permissions.Tenant.ManageSettings)]
    public Task<RegisterDto> UpdateRegister(Guid branchId, Guid registerId, SaveRegisterRequest request, CancellationToken ct) =>
        tenancy.UpdateRegisterAsync(branchId, registerId, request, ct);

    [HttpGet("devices")]
    [RequirePermission(Permissions.Tenant.ManageSettings)]
    public Task<IReadOnlyList<DeviceDto>> ListDevices(CancellationToken ct) => devices.ListAsync(ct);

    /// <summary>Registers a counter PC for PIN sign-in. The device token in the result is shown once.</summary>
    [HttpPost("devices")]
    [RequirePermission(Permissions.Tenant.ManageSettings)]
    public Task<RegisterDeviceResult> RegisterDevice(RegisterDeviceRequest request, CancellationToken ct) => devices.RegisterAsync(request, ct);

    [HttpPut("devices/{deviceId:guid}")]
    [RequirePermission(Permissions.Tenant.ManageSettings)]
    public Task<DeviceDto> UpdateDevice(Guid deviceId, UpdateDeviceRequest request, CancellationToken ct) => devices.UpdateAsync(deviceId, request, ct);

    /// <summary>This counter PC (X-Device-Token), or 204 when the request doesn't come from a registered PC.</summary>
    [HttpGet("devices/current")]
    public async Task<ActionResult<DeviceDto>> CurrentDevice([FromHeader(Name = "X-Device-Token")] string? deviceToken, CancellationToken ct) =>
        await devices.CurrentAsync(deviceToken, ct) is { } device ? device : NoContent();

    [HttpDelete("devices/{deviceId:guid}")]
    [RequirePermission(Permissions.Tenant.ManageSettings)]
    public async Task<IActionResult> RevokeDevice(Guid deviceId, CancellationToken ct)
    {
        await devices.RevokeAsync(deviceId, ct);
        return NoContent();
    }
}
