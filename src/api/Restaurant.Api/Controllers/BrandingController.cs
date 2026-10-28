using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Restaurant.Api.Auth;
using Restaurant.Application.Common.Exceptions;
using Restaurant.Application.Features.Branding;
using Restaurant.Contracts.Auth;
using Restaurant.Contracts.Branding;

namespace Restaurant.Api.Controllers;

[ApiController]
[Route("api")]
[Authorize]
public class BrandingController(BrandingService branding) : ControllerBase
{
    [HttpGet("tenant/branding")]
    public Task<BrandingDto> Get(CancellationToken ct) => branding.GetAsync(ct);

    [HttpPut("tenant/branding")]
    [RequirePermission(Permissions.Tenant.ManageBranding)]
    public Task<BrandingDto> Update(UpdateBrandingRequest request, CancellationToken ct) => branding.UpdateAsync(request, ct);

    [HttpPost("tenant/branding/logo")]
    [RequirePermission(Permissions.Tenant.ManageBranding)]
    [RequestSizeLimit(6 * 1024 * 1024)]
    public async Task<BrandingDto> UploadLogo(IFormFile? logo, CancellationToken ct)
    {
        if (logo is null || logo.Length == 0)
        {
            throw new ValidationAppException("logo", "Choose an image to upload.");
        }

        await using var stream = logo.OpenReadStream();
        return await branding.UpdateLogoAsync(stream, logo.FileName, logo.ContentType, logo.Length, ct);
    }

    [HttpGet("receipt-templates")]
    public Task<IReadOnlyList<ReceiptTemplateDto>> Templates(CancellationToken ct) => branding.ListTemplatesAsync(ct);
}
