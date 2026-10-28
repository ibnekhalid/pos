using Restaurant.Application.Common.Interfaces;
using Restaurant.Application.Features.Staff;
using Restaurant.Contracts.Enums;
using Restaurant.Domain.Constants;
using Restaurant.Domain.Entities;

namespace Restaurant.Application.Features.Onboarding;

/// <summary>
/// Everything a brand-new restaurant needs before its first order: first branch and register, the
/// system roles, the owner's membership, branding and the usual Pakistani payment methods. Taxes
/// are left for the owner to set up (they depend on the province).
/// </summary>
public class TenantProvisioner(IApplicationDbContext db)
{
    public (Tenant Tenant, Branch Branch, StaffMembership OwnerMembership) Provision(
        AppUser owner, string restaurantName, string? branchName, string? address, string? city, string? phone)
    {
        var tenant = new Tenant { Name = restaurantName.Trim() };

        var branch = new Branch
        {
            TenantId = tenant.Id,
            Name = string.IsNullOrWhiteSpace(branchName) ? "Main branch" : branchName.Trim(),
            Address = address?.Trim(),
            City = city?.Trim(),
            Phone = phone
        };

        var register = new Register
        {
            TenantId = tenant.Id,
            BranchId = branch.Id,
            Name = "Counter 1",
            IsDefault = true
        };

        var roles = SystemRoles.CreateFor(tenant.Id);
        var ownerRole = roles.Single(r => r.SystemKey == SystemRoleKeys.Owner);

        var membership = new StaffMembership
        {
            TenantId = tenant.Id,
            UserId = owner.Id,
            DisplayName = owner.FullName,
            Status = MembershipStatus.Active,
            AllBranches = true
        };
        membership.Roles.Add(new MembershipRole { TenantId = tenant.Id, MembershipId = membership.Id, RoleId = ownerRole.Id });

        var branding = new BrandingSettings
        {
            TenantId = tenant.Id,
            DisplayName = tenant.Name,
            DisplayAddress = address?.Trim(),
            DisplayPhone = phone,
            DefaultReceiptTemplateId = SystemReceiptTemplates.Thermal80mmId,
            DefaultKitchenTemplateId = SystemReceiptTemplates.Kitchen80mmId
        };

        (string Name, PaymentMethodKind Kind, bool RequiresReference)[] paymentMethods =
        [
            ("Cash", PaymentMethodKind.Cash, false),
            ("Card", PaymentMethodKind.Card, false),
            ("Bank transfer", PaymentMethodKind.BankTransfer, true),
            ("JazzCash", PaymentMethodKind.MobileWallet, true),
            ("Easypaisa", PaymentMethodKind.MobileWallet, true)
        ];

        db.Tenants.Add(tenant);
        db.Branches.Add(branch);
        db.Registers.Add(register);
        db.TenantRoles.AddRange(roles);
        db.Memberships.Add(membership);
        db.BrandingSettings.Add(branding);
        db.PaymentMethods.AddRange(paymentMethods.Select((p, i) => new PaymentMethodConfig
        {
            TenantId = tenant.Id,
            Name = p.Name,
            Kind = p.Kind,
            IsCash = p.Kind == PaymentMethodKind.Cash,
            RequiresReference = p.RequiresReference,
            SortOrder = i
        }));

        return (tenant, branch, membership);
    }
}
