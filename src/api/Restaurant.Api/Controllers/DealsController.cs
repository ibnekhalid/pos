using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Restaurant.Api.Auth;
using Restaurant.Application.Features.Deals;
using Restaurant.Contracts.Auth;
using Restaurant.Contracts.Deals;

namespace Restaurant.Api.Controllers;

[ApiController]
[Route("api/deals")]
[Authorize]
public class DealsController(DealService deals) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Deals.View, Permissions.Deals.Manage)]
    public Task<IReadOnlyList<DealDto>> List(CancellationToken ct) => deals.ListAsync(ct);

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.Deals.View, Permissions.Deals.Manage)]
    public Task<DealDto> Get(Guid id, CancellationToken ct) => deals.GetAsync(id, ct);

    [HttpPost]
    [RequirePermission(Permissions.Deals.Manage)]
    public Task<DealDto> Create(SaveDealRequest request, CancellationToken ct) => deals.SaveAsync(null, request, ct);

    [HttpPut("{id:guid}")]
    [RequirePermission(Permissions.Deals.Manage)]
    public Task<DealDto> Update(Guid id, SaveDealRequest request, CancellationToken ct) => deals.SaveAsync(id, request, ct);
}
