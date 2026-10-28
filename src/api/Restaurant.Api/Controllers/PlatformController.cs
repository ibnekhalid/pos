using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Restaurant.Application.Features.Platform;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Tenancy;

namespace Restaurant.Api.Controllers;

/// <summary>Platform administration. PlatformService checks the PlatformAdmin Identity role on every call.</summary>
[ApiController]
[Route("api/platform")]
[Authorize]
public class PlatformController(PlatformService platform) : ControllerBase
{
    [HttpGet("tenants")]
    public Task<IReadOnlyList<PlatformTenantDto>> Tenants(CancellationToken ct) => platform.ListTenantsAsync(ct);

    [HttpPost("tenants/{tenantId:guid}/suspend")]
    public async Task<IActionResult> Suspend(Guid tenantId, CancellationToken ct)
    {
        await platform.SetStatusAsync(tenantId, TenantStatus.Suspended, ct);
        return NoContent();
    }

    [HttpPost("tenants/{tenantId:guid}/activate")]
    public async Task<IActionResult> Activate(Guid tenantId, CancellationToken ct)
    {
        await platform.SetStatusAsync(tenantId, TenantStatus.Active, ct);
        return NoContent();
    }
}
