using Microsoft.EntityFrameworkCore;
using Restaurant.Application.Common.Exceptions;
using Restaurant.Application.Common.Interfaces;
using Restaurant.Application.Common.Services;
using Restaurant.Contracts.Customers;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Orders;
using Restaurant.Domain.Entities;

namespace Restaurant.Application.Features.Customers;

/// <summary>
/// Optional customer profiles (PLAN.md section 12): name, phone, address and notes only. A phone number
/// entered at the POS links the order to the profile (creating it if new).
/// </summary>
public class CustomerService(IApplicationDbContext db, ITenantContext tenant, AuditLogger audit, TimeProvider clock)
{
    public const string RemovedName = "Removed customer";

    public async Task<IReadOnlyList<CustomerDto>> SearchAsync(string? search, CancellationToken ct)
    {
        tenant.RequireTenantId();
        var query = db.Customers.Where(c => c.AnonymizedAt == null);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var phone = NormalizePhone(term);
            query = phone is { Length: >= 3 }
                ? query.Where(c => c.Phone != null && c.Phone.Contains(phone))
                : query.Where(c => c.Name.ToLower().Contains(term.ToLower()));
        }
        var customers = await query.OrderBy(c => c.Name).Take(50).ToListAsync(ct);
        return await ToDtosAsync(customers, ct);
    }

    public async Task<CustomerDetailDto> GetAsync(Guid id, CancellationToken ct)
    {
        tenant.RequireTenantId();
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Customer");
        var orders = await db.Orders.Where(o => o.CustomerId == id).OrderByDescending(o => o.CreatedAt).Take(100)
            .Select(o => new OrderSummaryDto(o.Id, o.OrderNumber, o.Type, o.Status, o.PaymentStatus, o.TableLabel, o.CustomerName,
                o.Total, o.Total > o.PaidTotal ? o.Total - o.PaidTotal : 0,
                o.Lines.Where(l => l.Status == OrderLineStatus.Active).Sum(l => l.Quantity), o.CreatedAt))
            .ToListAsync(ct);
        return new CustomerDetailDto((await ToDtosAsync([customer], ct))[0], orders);
    }

    public async Task<CustomerDto> SaveAsync(Guid? id, SaveCustomerRequest request, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 100) throw new ValidationAppException(nameof(request.Name), "Enter a name (up to 100 characters).");
        if (request.Address?.Length > 300 || request.Notes?.Length > 300) throw new ValidationAppException(nameof(request.Notes), "The address or note is too long.");
        var phone = NormalizePhone(request.Phone);
        if (request.Phone is { Length: > 0 } && phone is not { Length: >= 7 and <= 15 }) throw new ValidationAppException(nameof(request.Phone), "Enter a valid phone number.");
        if (phone is not null && await db.Customers.AnyAsync(c => c.Phone == phone && c.Id != id, ct))
        {
            throw new ConflictException("Another customer already has this phone number.");
        }

        Customer customer;
        if (id is { } existingId)
        {
            customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == existingId, ct) ?? throw new NotFoundException("Customer");
            if (customer.AnonymizedAt is not null) throw new ConflictException("This customer's details were removed.");
        }
        else
        {
            customer = new Customer { TenantId = tenantId };
            db.Customers.Add(customer);
        }
        customer.Name = request.Name.Trim();
        customer.Phone = phone;
        customer.Address = Blank(request.Address);
        customer.Notes = Blank(request.Notes);

        audit.Record(AuditActions.CustomerSaved, nameof(Customer), customer.Id);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (db.IsUniqueViolation(ex))
        {
            throw new ConflictException("Another customer already has this phone number.");
        }
        return (await ToDtosAsync([customer], ct))[0];
    }

    /// <summary>"Remove personal data": the profile and its orders keep no name, phone, address or notes.</summary>
    public async Task AnonymizeAsync(Guid id, CancellationToken ct)
    {
        tenant.RequireTenantId();
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Customer");
        customer.Name = RemovedName;
        customer.Phone = null;
        customer.Address = null;
        customer.Notes = null;
        customer.AnonymizedAt = clock.GetUtcNow();

        await db.Orders.Where(o => o.CustomerId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.CustomerName, (string?)null).SetProperty(o => o.CustomerPhone, (string?)null), ct);
        audit.Record(AuditActions.CustomerAnonymized, nameof(Customer), id);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The customer for an order: the given profile, or the one with this phone number (a new profile if
    /// none). Null for walk-ins without a phone. Added to the unit of work, saved with the order.
    /// </summary>
    public async Task<Customer?> ForOrderAsync(Guid? customerId, string? name, string? phone, CancellationToken ct)
    {
        if (customerId is { } id)
        {
            var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == id && c.AnonymizedAt == null, ct) ?? throw new NotFoundException("Customer");
            return customer;
        }

        var normalized = NormalizePhone(phone);
        if (normalized is not { Length: >= 7 and <= 15 }) return null;
        var existing = await db.Customers.FirstOrDefaultAsync(c => c.Phone == normalized, ct);
        if (existing is not null) return existing;

        var created = new Customer { TenantId = tenant.RequireTenantId(), Name = Blank(name) ?? normalized, Phone = normalized };
        db.Customers.Add(created);
        return created;
    }

    /// <summary>Digits only; +92 / 0092 become the local 0 prefix, so "+92 300-1234567" and "03001234567" match.</summary>
    public static string? NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("0092")) digits = "0" + digits[4..];
        else if (digits.StartsWith("92") && digits.Length == 12) digits = "0" + digits[2..];
        return digits.Length == 0 ? null : digits;
    }

    private async Task<IReadOnlyList<CustomerDto>> ToDtosAsync(IReadOnlyList<Customer> customers, CancellationToken ct)
    {
        var ids = customers.Select(c => c.Id).ToList();
        var stats = await db.Orders.Where(o => o.CustomerId != null && ids.Contains(o.CustomerId.Value) && o.Status == OrderStatus.Completed)
            .GroupBy(o => o.CustomerId!.Value)
            .Select(g => new { g.Key, Count = g.Count(), Spend = g.Sum(o => o.PaidTotal - o.RefundedTotal), Last = g.Max(o => o.CreatedAt) })
            .ToDictionaryAsync(x => x.Key, ct);
        return customers.Select(c =>
        {
            var s = stats.GetValueOrDefault(c.Id);
            return new CustomerDto(c.Id, c.Name, c.Phone, c.Address, c.Notes, s?.Count ?? 0, s?.Spend ?? 0, s?.Last, c.CreatedAt);
        }).ToList();
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
