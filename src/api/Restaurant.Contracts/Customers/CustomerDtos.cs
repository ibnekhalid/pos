using Restaurant.Contracts.Orders;

namespace Restaurant.Contracts.Customers;

/// <param name="OrderCount">Completed orders; TotalSpend is what they paid for them.</param>
public record CustomerDto(Guid Id, string Name, string? Phone, string? Address, string? Notes, int OrderCount, decimal TotalSpend,
    DateTimeOffset? LastOrderAt, DateTimeOffset CreatedAt);

public record CustomerSearchRequest(string? Search);

public record SaveCustomerRequest(string Name, string? Phone, string? Address, string? Notes);

public record CustomerDetailDto(CustomerDto Customer, IReadOnlyList<OrderSummaryDto> Orders);
