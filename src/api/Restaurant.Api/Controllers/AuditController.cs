using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Restaurant.Api.Auth;
using Restaurant.Application.Features.Audit;
using Restaurant.Contracts.Audit;
using Restaurant.Contracts.Auth;
using Restaurant.Contracts.Common;

namespace Restaurant.Api.Controllers;

[ApiController]
[Route("api/audit")]
[Authorize]
public class AuditController(AuditQueryService audit) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Reports.Audit)]
    public Task<PagedResult<AuditLogDto>> List(
        [FromQuery] string? action,
        [FromQuery] string? entityType,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default) =>
        audit.ListAsync(action, entityType, from, to, page, pageSize, ct);
}
