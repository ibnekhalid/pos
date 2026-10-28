using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Restaurant.Application.Common.Exceptions;
using Restaurant.Application.Common.Extensions;
using Restaurant.Application.Common.Interfaces;
using Restaurant.Application.Common.Security;
using Restaurant.Application.Common.Services;
using Restaurant.Contracts.Tenancy;
using Restaurant.Domain.Entities;

namespace Restaurant.Application.Features.Tenancy;

/// <summary>The restaurant itself, its branches (and their operating rules) and registers.</summary>
public class TenancyService(
    IApplicationDbContext db,
    ITenantContext tenant,
    MembershipAccessService access,
    AuditLogger audit,
    IValidator<UpdateTenantRequest> tenantValidator,
    IValidator<SaveBranchRequest> branchValidator,
    IValidator<BranchSettingsDto> settingsValidator,
    IValidator<SaveRegisterRequest> registerValidator)
{
    public async Task<TenantDto> GetTenantAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = tenant.RequireTenantId();
        var entity = await db.Tenants.FirstAsync(t => t.Id == tenantId, cancellationToken);
        return ToDto(entity);
    }

    public async Task<TenantDto> UpdateTenantAsync(UpdateTenantRequest request, CancellationToken cancellationToken = default)
    {
        await tenantValidator.ValidateAndThrowAppExceptionAsync(request, cancellationToken);
        var tenantId = tenant.RequireTenantId();
        var entity = await db.Tenants.FirstAsync(t => t.Id == tenantId, cancellationToken);
        var before = ToDto(entity);

        entity.Name = request.Name.Trim();
        entity.LegalName = request.LegalName?.Trim();
        entity.NtnNumber = request.NtnNumber?.Trim();
        entity.StrnNumber = request.StrnNumber?.Trim();
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        var after = ToDto(entity);
        audit.Record(AuditActions.TenantUpdated, nameof(Tenant), entity.Id, before, after);
        await db.SaveChangesAsync(cancellationToken);
        return after;
    }

    /// <summary>The branches the caller may use.</summary>
    public async Task<IReadOnlyList<BranchDto>> ListBranchesAsync(CancellationToken cancellationToken = default)
    {
        tenant.RequireTenantId();
        var allowed = tenant.AllowedBranchIds.ToList();
        var branches = await db.Branches.Where(b => allowed.Contains(b.Id)).OrderBy(b => b.CreatedAt).ToListAsync(cancellationToken);
        return branches.Select(ToDto).ToList();
    }

    public async Task<BranchDto> GetBranchAsync(Guid branchId, CancellationToken cancellationToken = default) =>
        ToDto(await GetAccessibleBranchAsync(branchId, cancellationToken));

    public async Task<BranchDto> CreateBranchAsync(SaveBranchRequest request, CancellationToken cancellationToken = default)
    {
        await branchValidator.ValidateAndThrowAppExceptionAsync(request, cancellationToken);
        var tenantId = tenant.RequireTenantId();
        await EnsureUniqueBranchNameAsync(request.Name, null, cancellationToken);

        var branch = new Branch { TenantId = tenantId };
        Apply(branch, request);
        db.Branches.Add(branch);
        db.Registers.Add(new Register { TenantId = tenantId, BranchId = branch.Id, Name = "Counter 1", IsDefault = true });

        audit.Record(AuditActions.BranchCreated, nameof(Branch), branch.Id, after: ToDto(branch), branchId: branch.Id);
        await db.SaveChangesAsync(cancellationToken);
        access.InvalidateTenant(tenantId);
        return ToDto(branch);
    }

    public async Task<BranchDto> UpdateBranchAsync(Guid branchId, SaveBranchRequest request, CancellationToken cancellationToken = default)
    {
        await branchValidator.ValidateAndThrowAppExceptionAsync(request, cancellationToken);
        var branch = await GetAccessibleBranchAsync(branchId, cancellationToken);
        await EnsureUniqueBranchNameAsync(request.Name, branchId, cancellationToken);

        var before = ToDto(branch);
        Apply(branch, request);
        branch.UpdatedAt = DateTimeOffset.UtcNow;

        audit.Record(AuditActions.BranchUpdated, nameof(Branch), branch.Id, before, ToDto(branch), branchId: branch.Id);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(branch);
    }

    public async Task<BranchDto> UpdateBranchSettingsAsync(Guid branchId, BranchSettingsDto request, CancellationToken cancellationToken = default)
    {
        await settingsValidator.ValidateAndThrowAppExceptionAsync(request, cancellationToken);
        var branch = await GetAccessibleBranchAsync(branchId, cancellationToken);
        var before = SettingsOf(branch);

        branch.TimeZoneId = request.TimeZoneId;
        branch.DayStartHour = request.DayStartHour;
        branch.ServiceChargePercent = request.ServiceChargePercent;
        branch.ServiceChargeAppliesTo = request.ServiceChargeAppliesTo;
        branch.Rounding = request.Rounding;
        branch.SessionScope = request.SessionScope;
        branch.CashDifferenceThreshold = request.CashDifferenceThreshold;
        branch.InsufficientStock = request.InsufficientStock;
        branch.LateTicketMinutes = request.LateTicketMinutes;
        branch.UpdatedAt = DateTimeOffset.UtcNow;

        audit.Record(AuditActions.BranchSettingsUpdated, nameof(Branch), branch.Id, before, SettingsOf(branch), branchId: branch.Id);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(branch);
    }

    public async Task<IReadOnlyList<RegisterDto>> ListRegistersAsync(Guid branchId, CancellationToken cancellationToken = default)
    {
        await GetAccessibleBranchAsync(branchId, cancellationToken);
        return await db.Registers
            .Where(r => r.BranchId == branchId)
            .OrderBy(r => r.CreatedAt)
            .Select(r => ToDto(r))
            .ToListAsync(cancellationToken);
    }

    public async Task<RegisterDto> CreateRegisterAsync(Guid branchId, SaveRegisterRequest request, CancellationToken cancellationToken = default)
    {
        await registerValidator.ValidateAndThrowAppExceptionAsync(request, cancellationToken);
        var branch = await GetAccessibleBranchAsync(branchId, cancellationToken);

        var register = new Register { TenantId = branch.TenantId, BranchId = branch.Id };
        db.Registers.Add(register);
        await ApplyAsync(register, request, cancellationToken);

        audit.Record(AuditActions.RegisterSaved, nameof(Register), register.Id, after: ToDto(register), branchId: branch.Id);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(register);
    }

    public async Task<RegisterDto> UpdateRegisterAsync(Guid branchId, Guid registerId, SaveRegisterRequest request, CancellationToken cancellationToken = default)
    {
        await registerValidator.ValidateAndThrowAppExceptionAsync(request, cancellationToken);
        await GetAccessibleBranchAsync(branchId, cancellationToken);
        var register = await db.Registers.FirstOrDefaultAsync(r => r.Id == registerId && r.BranchId == branchId, cancellationToken)
            ?? throw new NotFoundException("Register");

        var before = ToDto(register);
        await ApplyAsync(register, request, cancellationToken);
        register.UpdatedAt = DateTimeOffset.UtcNow;

        audit.Record(AuditActions.RegisterSaved, nameof(Register), register.Id, before, ToDto(register), branchId: branchId);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(register);
    }

    /// <summary>
    /// A branch of the current tenant the caller may use. Another tenant's branch doesn't exist (404);
    /// one of this tenant's branches the caller isn't assigned to is forbidden (403).
    /// </summary>
    public async Task<Branch> GetAccessibleBranchAsync(Guid branchId, CancellationToken cancellationToken)
    {
        tenant.RequireTenantId();
        var branch = await db.Branches.FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken)
            ?? throw new NotFoundException("Branch");

        if (!tenant.CanAccessBranch(branch.Id))
        {
            throw new ForbiddenAccessException("You don't have access to this branch.");
        }
        return branch;
    }

    private async Task ApplyAsync(Register register, SaveRegisterRequest request, CancellationToken cancellationToken)
    {
        var otherDefaults = await db.Registers
            .Where(r => r.BranchId == register.BranchId && r.Id != register.Id && r.IsDefault)
            .ToListAsync(cancellationToken);

        if (!request.IsDefault && register.IsDefault && otherDefaults.Count == 0)
        {
            throw new ValidationAppException(nameof(request.IsDefault), "A branch needs a default register. Make another register the default first.");
        }
        if (request.IsDefault && !request.IsActive)
        {
            throw new ValidationAppException(nameof(request.IsActive), "The default register can't be deactivated.");
        }

        if (request.IsDefault)
        {
            foreach (var other in otherDefaults)
            {
                other.IsDefault = false;
            }
        }

        register.Name = request.Name.Trim();
        register.IsDefault = request.IsDefault;
        register.IsActive = request.IsActive;
        register.ReceiptPrinterName = request.ReceiptPrinterName?.Trim();
        register.KitchenPrinterName = request.KitchenPrinterName?.Trim();
        register.CashDrawerEnabled = request.CashDrawerEnabled;
    }

    private async Task EnsureUniqueBranchNameAsync(string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        var trimmed = name.Trim();
        if (await db.Branches.AnyAsync(b => b.Name == trimmed && b.Id != exceptId, cancellationToken))
        {
            throw new ConflictException("A branch with this name already exists.");
        }
    }

    private static void Apply(Branch branch, SaveBranchRequest request)
    {
        branch.Name = request.Name.Trim();
        branch.Address = request.Address?.Trim();
        branch.City = request.City?.Trim();
        branch.Phone = request.Phone?.Trim();
        branch.OrderNumberPrefix = request.OrderNumberPrefix.Trim().ToUpperInvariant();
        branch.IsActive = request.IsActive;
    }

    private static TenantDto ToDto(Tenant t) => new(t.Id, t.Name, t.LegalName, t.NtnNumber, t.StrnNumber, t.CurrencyCode, t.Status);

    private static BranchSettingsDto SettingsOf(Branch b) => new(
        b.TimeZoneId, b.DayStartHour, b.ServiceChargePercent, b.ServiceChargeAppliesTo, b.Rounding,
        b.SessionScope, b.CashDifferenceThreshold, b.InsufficientStock, b.LateTicketMinutes);

    public static BranchDto ToDto(Branch b) => new(b.Id, b.Name, b.Address, b.City, b.Phone, b.OrderNumberPrefix, b.IsActive, SettingsOf(b));

    private static RegisterDto ToDto(Register r) =>
        new(r.Id, r.BranchId, r.Name, r.IsDefault, r.IsActive, r.ReceiptPrinterName, r.KitchenPrinterName, r.CashDrawerEnabled);
}
