using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Restaurant.Application.Common.Exceptions;
using Restaurant.Application.Common.Extensions;
using Restaurant.Application.Common.Interfaces;
using Restaurant.Application.Common.Services;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Tenancy;
using Restaurant.Domain.Entities;

namespace Restaurant.Application.Features.Settings;

public class SaveTaxRateRequestValidator : AbstractValidator<SaveTaxRateRequest>
{
    public SaveTaxRateRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(60);
        RuleFor(x => x.Percent).InclusiveBetween(0m, 100m);
        RuleFor(x => x.AppliesTo).Must(t => t != OrderTypes.None && (t & ~OrderTypes.All) == 0)
            .WithMessage("Choose at least one order type.");
        RuleFor(x => x.PaymentScope).IsInEnum();
    }
}

public class SavePaymentMethodRequestValidator : AbstractValidator<SavePaymentMethodRequest>
{
    public SavePaymentMethodRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(60);
        RuleFor(x => x.Kind).IsInEnum();
        RuleFor(x => x.SortOrder).InclusiveBetween(0, 1000);
    }
}

/// <summary>Tax rates. They are deactivated rather than deleted, because past orders refer to them.</summary>
public class TaxRateService(IApplicationDbContext db, ITenantContext tenant, AuditLogger audit, IValidator<SaveTaxRateRequest> validator)
{
    public async Task<IReadOnlyList<TaxRateDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        tenant.RequireTenantId();
        return await db.TaxRates.OrderBy(t => t.Name).Select(t => ToDto(t)).ToListAsync(cancellationToken);
    }

    public async Task<TaxRateDto> CreateAsync(SaveTaxRateRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = tenant.RequireTenantId();
        await ValidateAsync(request, cancellationToken);

        var rate = new TaxRate { TenantId = tenantId };
        Apply(rate, request);
        db.TaxRates.Add(rate);

        audit.Record(AuditActions.TaxRateSaved, nameof(TaxRate), rate.Id, after: ToDto(rate));
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(rate);
    }

    public async Task<TaxRateDto> UpdateAsync(Guid id, SaveTaxRateRequest request, CancellationToken cancellationToken = default)
    {
        tenant.RequireTenantId();
        var rate = await db.TaxRates.FirstOrDefaultAsync(t => t.Id == id, cancellationToken) ?? throw new NotFoundException("Tax rate");
        await ValidateAsync(request, cancellationToken);

        var before = ToDto(rate);
        Apply(rate, request);
        rate.UpdatedAt = DateTimeOffset.UtcNow;

        audit.Record(AuditActions.TaxRateSaved, nameof(TaxRate), rate.Id, before, ToDto(rate));
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(rate);
    }

    private async Task ValidateAsync(SaveTaxRateRequest request, CancellationToken cancellationToken)
    {
        await validator.ValidateAndThrowAppExceptionAsync(request, cancellationToken);
        if (request.BranchId is { } branchId && !await db.Branches.AnyAsync(b => b.Id == branchId, cancellationToken))
        {
            throw new NotFoundException("Branch");
        }
    }

    private static void Apply(TaxRate rate, SaveTaxRateRequest request)
    {
        rate.Name = request.Name.Trim();
        rate.Percent = request.Percent;
        rate.IsInclusive = request.IsInclusive;
        rate.AppliesTo = request.AppliesTo;
        rate.PaymentScope = request.PaymentScope;
        rate.BranchId = request.BranchId;
        rate.IsActive = request.IsActive;
    }

    private static TaxRateDto ToDto(TaxRate t) => new(t.Id, t.Name, t.Percent, t.IsInclusive, t.AppliesTo, t.PaymentScope, t.BranchId, t.IsActive);
}

/// <summary>Payment methods. Deactivated rather than deleted, because past payments refer to them.</summary>
public class PaymentMethodService(IApplicationDbContext db, ITenantContext tenant, AuditLogger audit, IValidator<SavePaymentMethodRequest> validator)
{
    public async Task<IReadOnlyList<PaymentMethodDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        tenant.RequireTenantId();
        return await db.PaymentMethods.OrderBy(p => p.SortOrder).ThenBy(p => p.Name).Select(p => ToDto(p)).ToListAsync(cancellationToken);
    }

    public async Task<PaymentMethodDto> CreateAsync(SavePaymentMethodRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = tenant.RequireTenantId();
        await validator.ValidateAndThrowAppExceptionAsync(request, cancellationToken);
        await EnsureUniqueNameAsync(request.Name, null, cancellationToken);

        var method = new PaymentMethodConfig { TenantId = tenantId };
        Apply(method, request);
        db.PaymentMethods.Add(method);

        audit.Record(AuditActions.PaymentMethodSaved, nameof(PaymentMethodConfig), method.Id, after: ToDto(method));
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(method);
    }

    public async Task<PaymentMethodDto> UpdateAsync(Guid id, SavePaymentMethodRequest request, CancellationToken cancellationToken = default)
    {
        tenant.RequireTenantId();
        var method = await db.PaymentMethods.FirstOrDefaultAsync(p => p.Id == id, cancellationToken) ?? throw new NotFoundException("Payment method");
        await validator.ValidateAndThrowAppExceptionAsync(request, cancellationToken);
        await EnsureUniqueNameAsync(request.Name, id, cancellationToken);

        var before = ToDto(method);
        Apply(method, request);
        method.UpdatedAt = DateTimeOffset.UtcNow;

        audit.Record(AuditActions.PaymentMethodSaved, nameof(PaymentMethodConfig), method.Id, before, ToDto(method));
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(method);
    }

    private async Task EnsureUniqueNameAsync(string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        var trimmed = name.Trim();
        if (await db.PaymentMethods.AnyAsync(p => p.Name == trimmed && p.Id != exceptId, cancellationToken))
        {
            throw new ConflictException("A payment method with this name already exists.");
        }
    }

    private static void Apply(PaymentMethodConfig method, SavePaymentMethodRequest request)
    {
        method.Name = request.Name.Trim();
        method.Kind = request.Kind;
        method.IsCash = request.Kind == PaymentMethodKind.Cash;
        method.RequiresReference = request.RequiresReference;
        method.SortOrder = request.SortOrder;
        method.IsActive = request.IsActive;
    }

    private static PaymentMethodDto ToDto(PaymentMethodConfig p) => new(p.Id, p.Name, p.Kind, p.IsCash, p.RequiresReference, p.SortOrder, p.IsActive);
}
