using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Restaurant.Application.Common.Interfaces;
using Restaurant.Application.Common.Security;
using Restaurant.Application.Common.Services;
using Restaurant.Application.Features.Approvals;
using Restaurant.Application.Features.Audit;
using Restaurant.Application.Features.Customers;
using Restaurant.Application.Features.Deals;
using Restaurant.Application.Features.Inventory;
using Restaurant.Application.Features.Kitchen;
using Restaurant.Application.Features.Menu;
using Restaurant.Application.Features.Orders;
using Restaurant.Application.Features.Payments;
using Restaurant.Application.Features.Receipts;
using Restaurant.Application.Features.Reports;
using Restaurant.Application.Features.Sessions;
using Restaurant.Application.Features.Tables;
using Restaurant.Application.Features.Auth;
using Restaurant.Application.Features.Branding;
using Restaurant.Application.Features.Onboarding;
using Restaurant.Application.Features.Platform;
using Restaurant.Application.Features.Settings;
using Restaurant.Application.Features.Staff;
using Restaurant.Application.Features.Tenancy;

namespace Restaurant.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);

        // Filled in per request by the API's tenant-resolution middleware.
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddSingleton<MembershipAccessCache>();
        services.AddScoped<MembershipAccessService>();
        services.AddScoped<AuditLogger>();

        services.AddScoped<TenantProvisioner>();
        services.AddScoped<AuthService>();
        services.AddScoped<TenancyService>();
        services.AddScoped<TaxRateService>();
        services.AddScoped<PaymentMethodService>();
        services.AddScoped<DeviceService>();
        services.AddScoped<RoleService>();
        services.AddScoped<StaffService>();
        services.AddScoped<BrandingService>();
        services.AddScoped<AuditQueryService>();
        services.AddScoped<PlatformService>();

        services.AddScoped<ApprovalService>();
        services.AddScoped<MenuService>();
        services.AddScoped<TableService>();
        services.AddScoped<SessionService>();
        services.AddScoped<OrderProgress>();
        services.AddScoped<OrderService>();
        services.AddScoped<KitchenService>();
        services.AddScoped<PaymentService>();
        services.AddScoped<ReceiptService>();
        services.AddScoped<StockService>();
        services.AddScoped<DealService>();
        services.AddScoped<CustomerService>();
        services.AddScoped<ReportService>();
        services.AddScoped<PurchasingService>();
        services.AddScoped<ReservationService>();

        return services;
    }
}
