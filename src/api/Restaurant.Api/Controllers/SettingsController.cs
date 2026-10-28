using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Restaurant.Api.Auth;
using Restaurant.Application.Features.Settings;
using Restaurant.Contracts.Auth;
using Restaurant.Contracts.Tenancy;

namespace Restaurant.Api.Controllers;

/// <summary>Tax rates and payment methods. Every member can read them (the POS needs them); only settings managers change them.</summary>
[ApiController]
[Route("api")]
[Authorize]
public class SettingsController(TaxRateService taxRates, PaymentMethodService paymentMethods) : ControllerBase
{
    [HttpGet("tax-rates")]
    public Task<IReadOnlyList<TaxRateDto>> ListTaxRates(CancellationToken ct) => taxRates.ListAsync(ct);

    [HttpPost("tax-rates")]
    [RequirePermission(Permissions.Tenant.ManageSettings)]
    public Task<TaxRateDto> CreateTaxRate(SaveTaxRateRequest request, CancellationToken ct) => taxRates.CreateAsync(request, ct);

    [HttpPut("tax-rates/{id:guid}")]
    [RequirePermission(Permissions.Tenant.ManageSettings)]
    public Task<TaxRateDto> UpdateTaxRate(Guid id, SaveTaxRateRequest request, CancellationToken ct) => taxRates.UpdateAsync(id, request, ct);

    [HttpGet("payment-methods")]
    public Task<IReadOnlyList<PaymentMethodDto>> ListPaymentMethods(CancellationToken ct) => paymentMethods.ListAsync(ct);

    [HttpPost("payment-methods")]
    [RequirePermission(Permissions.Tenant.ManageSettings)]
    public Task<PaymentMethodDto> CreatePaymentMethod(SavePaymentMethodRequest request, CancellationToken ct) => paymentMethods.CreateAsync(request, ct);

    [HttpPut("payment-methods/{id:guid}")]
    [RequirePermission(Permissions.Tenant.ManageSettings)]
    public Task<PaymentMethodDto> UpdatePaymentMethod(Guid id, SavePaymentMethodRequest request, CancellationToken ct) => paymentMethods.UpdateAsync(id, request, ct);
}
