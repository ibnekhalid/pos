using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Restaurant.Api.Auth;
using Restaurant.Application.Features.Customers;
using Restaurant.Contracts.Auth;
using Restaurant.Contracts.Customers;

namespace Restaurant.Api.Controllers;

[ApiController]
[Route("api/customers")]
[Authorize]
public class CustomersController(CustomerService customers) : ControllerBase
{
    /// <summary>By phone (digits) or name, sent in the body so phone numbers stay out of URLs and logs. Cashiers can look customers up at the POS.</summary>
    [HttpPost("search")]
    [RequirePermission(Permissions.Customers.View, Permissions.Customers.Manage, Permissions.Orders.Create)]
    public Task<IReadOnlyList<CustomerDto>> Search(CustomerSearchRequest request, CancellationToken ct) => customers.SearchAsync(request.Search, ct);

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.Customers.View, Permissions.Customers.Manage)]
    public Task<CustomerDetailDto> Get(Guid id, CancellationToken ct) => customers.GetAsync(id, ct);

    [HttpPost]
    [RequirePermission(Permissions.Customers.Manage)]
    public Task<CustomerDto> Create(SaveCustomerRequest request, CancellationToken ct) => customers.SaveAsync(null, request, ct);

    [HttpPut("{id:guid}")]
    [RequirePermission(Permissions.Customers.Manage)]
    public Task<CustomerDto> Update(Guid id, SaveCustomerRequest request, CancellationToken ct) => customers.SaveAsync(id, request, ct);

    /// <summary>Removes the name, phone, address and notes from the profile and its orders.</summary>
    [HttpPost("{id:guid}/remove-personal-data")]
    [RequirePermission(Permissions.Customers.Manage)]
    public async Task<IActionResult> Anonymize(Guid id, CancellationToken ct)
    {
        await customers.AnonymizeAsync(id, ct);
        return NoContent();
    }
}
