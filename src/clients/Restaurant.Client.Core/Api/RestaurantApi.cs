using Restaurant.Client.Core.Auth;
using Restaurant.Contracts.Audit;
using Restaurant.Contracts.Auth;
using Restaurant.Contracts.Branding;
using Restaurant.Contracts.Common;
using Restaurant.Contracts.Customers;
using Restaurant.Contracts.Deals;
using Restaurant.Contracts.Enums;
using Restaurant.Contracts.Inventory;
using Restaurant.Contracts.Kitchen;
using Restaurant.Contracts.Menu;
using Restaurant.Contracts.Orders;
using Restaurant.Contracts.Receipts;
using Restaurant.Contracts.Reports;
using Restaurant.Contracts.Sessions;
using Restaurant.Contracts.Tables;
using Restaurant.Contracts.Staff;
using Restaurant.Contracts.System;
using Restaurant.Contracts.Tenancy;

namespace Restaurant.Client.Core.Api;

/// <summary>Typed calls for every API endpoint the screens use. Sign-in calls also update the session.</summary>
public class RestaurantApi(ApiClient api, SessionStore session)
{
    public const string DeviceTokenHeader = "X-Device-Token";

    public string? ResolveUrl(string? path) => api.ResolveUrl(path);

    public Task<ApiInfoDto> GetInfoAsync() => api.SendAnonymousAsync<ApiInfoDto>(HttpMethod.Get, "api/info", null);

    /// <summary>The newest desktop app (version and installer link), for the update notice.</summary>
    public Task<DesktopReleaseDto> GetDesktopReleaseAsync() =>
        api.SendAnonymousAsync<DesktopReleaseDto>(HttpMethod.Get, "api/desktop-release", null);

    // ------------------------------------------------------------ sign-in

    public async Task<AuthResultDto> LoginAsync(LoginRequest request)
    {
        var result = await api.SendAnonymousAsync<AuthResultDto>(HttpMethod.Post, "api/auth/login", request);
        await session.ApplySignInAsync(result, request.RememberMe);
        return result;
    }

    public async Task<AuthResultDto> RegisterOwnerAsync(RegisterOwnerRequest request)
    {
        var result = await api.SendAnonymousAsync<AuthResultDto>(HttpMethod.Post, "api/auth/register-owner", request);
        await session.ApplySignInAsync(result, rememberMe: true);
        return result;
    }

    public async Task<AuthResultDto> AcceptInviteAsync(AcceptInviteRequest request)
    {
        var result = await api.SendAnonymousAsync<AuthResultDto>(HttpMethod.Post, "api/auth/accept-invite", request);
        await session.ApplySignInAsync(result, rememberMe: true);
        return result;
    }

    public async Task<AuthResultDto> SelectTenantAsync(Guid tenantId)
    {
        var result = await api.PostAsync<AuthResultDto>("api/auth/select-tenant", new SelectTenantRequest(tenantId, session.RememberMe));
        await session.ApplySignInAsync(result, session.RememberMe);
        return result;
    }

    public Task<IReadOnlyList<DeviceStaffDto>> GetDeviceStaffAsync(string deviceToken) =>
        api.SendAnonymousAsync<IReadOnlyList<DeviceStaffDto>>(HttpMethod.Get, "api/auth/device-staff", null,
            new Dictionary<string, string> { [DeviceTokenHeader] = deviceToken });

    public async Task<AuthResultDto> PinLoginAsync(string deviceToken, Guid membershipId, string pin)
    {
        var result = await api.SendAnonymousAsync<AuthResultDto>(HttpMethod.Post, "api/auth/pin-login", new PinLoginRequest(deviceToken, membershipId, pin));
        await session.ApplySignInAsync(result, rememberMe: false, pinSession: true);
        return result;
    }

    public Task ForgotPasswordAsync(string email) =>
        api.SendAnonymousAsync<object?>(HttpMethod.Post, "api/auth/forgot-password", new ForgotPasswordRequest(email));

    public Task ResetPasswordAsync(ResetPasswordRequest request) =>
        api.SendAnonymousAsync<object?>(HttpMethod.Post, "api/auth/reset-password", request);

    public Task ChangePasswordAsync(ChangePasswordRequest request) => api.PostAsync("api/auth/change-password", request);

    /// <summary>Revokes the refresh token on the server (best effort) and forgets the session here.</summary>
    public async Task LogoutAsync()
    {
        if (session.Current?.RefreshToken is { } refreshToken)
        {
            try
            {
                await api.SendAnonymousAsync<object?>(HttpMethod.Post, "api/auth/logout", new RefreshTokenRequest(refreshToken));
            }
            catch
            {
                // Offline: the token expires on its own.
            }
        }
        await session.SignOutAsync();
    }

    // ------------------------------------------------------------ me

    public Task<MeDto> GetMeAsync() => api.GetAsync<MeDto>("api/me");
    public Task SetPinAsync(SetPinRequest request) => api.PutAsync("api/me/pin", request);

    // ------------------------------------------------------------ restaurant, branches, registers, devices

    public Task<TenantDto> GetTenantAsync() => api.GetAsync<TenantDto>("api/tenant");
    public Task<TenantDto> UpdateTenantAsync(UpdateTenantRequest request) => api.PutAsync<TenantDto>("api/tenant", request);

    public Task<IReadOnlyList<BranchDto>> GetBranchesAsync() => api.GetAsync<IReadOnlyList<BranchDto>>("api/branches");
    public Task<BranchDto> GetBranchAsync(Guid id) => api.GetAsync<BranchDto>($"api/branches/{id}");
    public Task<BranchDto> CreateBranchAsync(SaveBranchRequest request) => api.PostAsync<BranchDto>("api/branches", request);
    public Task<BranchDto> UpdateBranchAsync(Guid id, SaveBranchRequest request) => api.PutAsync<BranchDto>($"api/branches/{id}", request);
    public Task<BranchDto> UpdateBranchSettingsAsync(Guid id, BranchSettingsDto request) => api.PutAsync<BranchDto>($"api/branches/{id}/settings", request);

    public Task<IReadOnlyList<RegisterDto>> GetRegistersAsync(Guid branchId) => api.GetAsync<IReadOnlyList<RegisterDto>>($"api/branches/{branchId}/registers");
    public Task<RegisterDto> CreateRegisterAsync(Guid branchId, SaveRegisterRequest request) => api.PostAsync<RegisterDto>($"api/branches/{branchId}/registers", request);
    public Task<RegisterDto> UpdateRegisterAsync(Guid branchId, Guid id, SaveRegisterRequest request) => api.PutAsync<RegisterDto>($"api/branches/{branchId}/registers/{id}", request);

    public Task<IReadOnlyList<DeviceDto>> GetDevicesAsync() => api.GetAsync<IReadOnlyList<DeviceDto>>("api/devices");
    public Task<RegisterDeviceResult> RegisterDeviceAsync(RegisterDeviceRequest request) => api.PostAsync<RegisterDeviceResult>("api/devices", request);
    public Task RevokeDeviceAsync(Guid id) => api.DeleteAsync($"api/devices/{id}");
    public Task<DeviceDto> UpdateDeviceAsync(Guid id, UpdateDeviceRequest request) => api.PutAsync<DeviceDto>($"api/devices/{id}", request);

    /// <summary>This counter PC as the server knows it (its register), or null if it isn't registered there.</summary>
    public Task<DeviceDto?> GetCurrentDeviceAsync(string deviceToken) =>
        api.GetAsync<DeviceDto?>("api/devices/current", new Dictionary<string, string> { [DeviceTokenHeader] = deviceToken });

    // ------------------------------------------------------------ taxes, payment methods

    public Task<IReadOnlyList<TaxRateDto>> GetTaxRatesAsync() => api.GetAsync<IReadOnlyList<TaxRateDto>>("api/tax-rates");
    public Task<TaxRateDto> CreateTaxRateAsync(SaveTaxRateRequest request) => api.PostAsync<TaxRateDto>("api/tax-rates", request);
    public Task<TaxRateDto> UpdateTaxRateAsync(Guid id, SaveTaxRateRequest request) => api.PutAsync<TaxRateDto>($"api/tax-rates/{id}", request);

    public Task<IReadOnlyList<PaymentMethodDto>> GetPaymentMethodsAsync() => api.GetAsync<IReadOnlyList<PaymentMethodDto>>("api/payment-methods");
    public Task<PaymentMethodDto> CreatePaymentMethodAsync(SavePaymentMethodRequest request) => api.PostAsync<PaymentMethodDto>("api/payment-methods", request);
    public Task<PaymentMethodDto> UpdatePaymentMethodAsync(Guid id, SavePaymentMethodRequest request) => api.PutAsync<PaymentMethodDto>($"api/payment-methods/{id}", request);

    // ------------------------------------------------------------ staff and roles

    public Task<IReadOnlyList<StaffMemberDto>> GetStaffAsync() => api.GetAsync<IReadOnlyList<StaffMemberDto>>("api/staff");
    public Task<StaffMemberDto> GetStaffMemberAsync(Guid id) => api.GetAsync<StaffMemberDto>($"api/staff/{id}");
    public Task<StaffInviteDto> CreateStaffAsync(CreateStaffRequest request) => api.PostAsync<StaffInviteDto>("api/staff", request);
    public Task<StaffMemberDto> UpdateStaffAsync(Guid id, UpdateStaffRequest request) => api.PutAsync<StaffMemberDto>($"api/staff/{id}", request);
    public Task<StaffInviteDto> ReissueInviteAsync(Guid id) => api.PostAsync<StaffInviteDto>($"api/staff/{id}/invite", new { });
    public Task<StaffMemberDto> TransferOwnershipAsync(Guid id, TransferOwnershipRequest request) => api.PostAsync<StaffMemberDto>($"api/staff/{id}/transfer-ownership", request);

    public Task<IReadOnlyList<PermissionDto>> GetPermissionsAsync() => api.GetAsync<IReadOnlyList<PermissionDto>>("api/permissions");
    public Task<IReadOnlyList<RoleDto>> GetRolesAsync() => api.GetAsync<IReadOnlyList<RoleDto>>("api/roles");
    public Task<RoleDto> CreateRoleAsync(SaveRoleRequest request) => api.PostAsync<RoleDto>("api/roles", request);
    public Task<RoleDto> UpdateRoleAsync(Guid id, SaveRoleRequest request) => api.PutAsync<RoleDto>($"api/roles/{id}", request);
    public Task DeleteRoleAsync(Guid id) => api.DeleteAsync($"api/roles/{id}");

    // ------------------------------------------------------------ branding, audit

    public Task<BrandingDto> GetBrandingAsync() => api.GetAsync<BrandingDto>("api/tenant/branding");
    public Task<BrandingDto> UpdateBrandingAsync(UpdateBrandingRequest request) => api.PutAsync<BrandingDto>("api/tenant/branding", request);
    public Task<BrandingDto> UploadLogoAsync(byte[] content, string fileName, string contentType) =>
        api.UploadAsync<BrandingDto>("api/tenant/branding/logo", "logo", content, fileName, contentType);

    public Task<PagedResult<AuditLogDto>> GetAuditAsync(int page, int pageSize = 50) =>
        api.GetAsync<PagedResult<AuditLogDto>>($"api/audit?page={page}&pageSize={pageSize}");

    // ------------------------------------------------------------ menu

    public Task<PosMenuDto> GetPosMenuAsync() => api.GetAsync<PosMenuDto>("api/menu/pos");
    public Task<IReadOnlyList<MenuItemDto>> GetMenuItemsAsync() => api.GetAsync<IReadOnlyList<MenuItemDto>>("api/menu/items");
    public Task<MenuItemDto> GetMenuItemAsync(Guid id) => api.GetAsync<MenuItemDto>($"api/menu/items/{id}");
    public Task<MenuItemDto> SaveMenuItemAsync(Guid? id, SaveMenuItemRequest request) =>
        id is null ? api.PostAsync<MenuItemDto>("api/menu/items", request) : api.PutAsync<MenuItemDto>($"api/menu/items/{id}", request);
    public Task SetAvailabilityAsync(Guid itemId, SetAvailabilityRequest request) => api.PutAsync($"api/menu/items/{itemId}/availability", request);
    public Task<IReadOnlyList<MenuCategoryDto>> GetCategoriesAsync() => api.GetAsync<IReadOnlyList<MenuCategoryDto>>("api/menu/categories");
    public Task<MenuCategoryDto> SaveCategoryAsync(Guid? id, SaveMenuCategoryRequest request) =>
        id is null ? api.PostAsync<MenuCategoryDto>("api/menu/categories", request) : api.PutAsync<MenuCategoryDto>($"api/menu/categories/{id}", request);
    public Task<IReadOnlyList<ModifierGroupDto>> GetModifierGroupsAsync() => api.GetAsync<IReadOnlyList<ModifierGroupDto>>("api/menu/modifier-groups");
    public Task<ModifierGroupDto> SaveModifierGroupAsync(Guid? id, SaveModifierGroupRequest request) =>
        id is null ? api.PostAsync<ModifierGroupDto>("api/menu/modifier-groups", request) : api.PutAsync<ModifierGroupDto>($"api/menu/modifier-groups/{id}", request);
    public Task<IReadOnlyList<KitchenStationDto>> GetStationsAsync() => api.GetAsync<IReadOnlyList<KitchenStationDto>>("api/menu/stations");
    public Task<KitchenStationDto> SaveStationAsync(Guid? id, SaveKitchenStationRequest request) =>
        id is null ? api.PostAsync<KitchenStationDto>("api/menu/stations", request) : api.PutAsync<KitchenStationDto>($"api/menu/stations/{id}", request);

    // ------------------------------------------------------------ tables

    public Task<IReadOnlyList<DiningAreaDto>> GetAreasAsync() => api.GetAsync<IReadOnlyList<DiningAreaDto>>("api/dining-areas");
    public Task<DiningAreaDto> SaveAreaAsync(Guid? id, SaveDiningAreaRequest request) =>
        id is null ? api.PostAsync<DiningAreaDto>("api/dining-areas", request) : api.PutAsync<DiningAreaDto>($"api/dining-areas/{id}", request);
    public Task<IReadOnlyList<TableDto>> GetTablesAsync() => api.GetAsync<IReadOnlyList<TableDto>>("api/tables");
    public Task<TableDto> SaveTableAsync(Guid? id, SaveTableRequest request) =>
        id is null ? api.PostAsync<TableDto>("api/tables", request) : api.PutAsync<TableDto>($"api/tables/{id}", request);

    // ------------------------------------------------------------ business day

    /// <summary>The open business day of the selected branch, or null.</summary>
    public Task<BusinessSessionDto?> GetCurrentSessionAsync() => api.GetAsync<BusinessSessionDto?>("api/sessions/current");
    public Task<BusinessSessionDto> StartSessionAsync(StartSessionRequest request) => api.PostAsync<BusinessSessionDto>("api/sessions/start", request);
    public Task<BusinessSessionDto> OpenRegisterAsync(OpenRegisterRequest request) => api.PostAsync<BusinessSessionDto>("api/sessions/current/registers", request);
    public Task<CashMovementDto> RecordCashAsync(RecordCashMovementRequest request) => api.PostAsync<CashMovementDto>("api/sessions/current/cash-movements", request);
    public Task<IReadOnlyList<CashMovementDto>> GetCashMovementsAsync(Guid sessionId) => api.GetAsync<IReadOnlyList<CashMovementDto>>($"api/sessions/{sessionId}/cash-movements");
    public Task<ClosePreviewDto> GetClosePreviewAsync() => api.GetAsync<ClosePreviewDto>("api/sessions/current/close-preview");
    public Task<ClosedSessionDto> CloseSessionAsync(CloseSessionRequest request) => api.PostAsync<ClosedSessionDto>("api/sessions/current/close", request);
    public Task<BusinessSessionDto> ReopenSessionAsync(Guid id, string reason) => api.PostAsync<BusinessSessionDto>($"api/sessions/{id}/reopen", new ReopenSessionRequest(reason));
    public Task<IReadOnlyList<SessionHistoryItemDto>> GetSessionHistoryAsync() => api.GetAsync<IReadOnlyList<SessionHistoryItemDto>>("api/sessions/history");
    public Task<ClosedSessionDto> GetSessionReportAsync(Guid id) => api.GetAsync<ClosedSessionDto>($"api/sessions/{id}/report");

    // ------------------------------------------------------------ reports

    public Task<SalesReportDto> GetSalesReportAsync(ReportFilter filter) => api.GetAsync<SalesReportDto>("api/reports/sales" + Query(filter));
    public Task<OrdersReportDto> GetOrdersReportAsync(ReportFilter filter) => api.GetAsync<OrdersReportDto>("api/reports/orders" + Query(filter));
    public Task<ProductsReportDto> GetProductsReportAsync(ReportFilter filter) => api.GetAsync<ProductsReportDto>("api/reports/products" + Query(filter));
    public Task<PaymentsReportDto> GetPaymentsReportAsync(ReportFilter filter) => api.GetAsync<PaymentsReportDto>("api/reports/payments" + Query(filter));
    public Task<InventoryReportDto> GetInventoryReportAsync(ReportFilter filter) => api.GetAsync<InventoryReportDto>("api/reports/inventory" + Query(filter));
    public Task<IReadOnlyList<LowStockDto>> GetLowStockAsync() => api.GetAsync<IReadOnlyList<LowStockDto>>("api/inventory/low-stock");
    public Task<MyShiftDto?> GetMyShiftAsync() => api.GetAsync<MyShiftDto?>("api/reports/my-shift");
    public Task<IReadOnlyList<ReportStaffDto>> GetReportStaffAsync() => api.GetAsync<IReadOnlyList<ReportStaffDto>>("api/reports/staff");

    private static string Query(ReportFilter f)
    {
        var parts = new List<string> { $"from={f.From:yyyy-MM-dd}", $"to={f.To:yyyy-MM-dd}" };
        if (f.AllBranches) parts.Add("allBranches=true");
        if (f.SessionId is { } session) parts.Add($"sessionId={session}");
        if (f.OrderType is { } type) parts.Add($"orderType={type}");
        if (f.CashierId is { } cashier) parts.Add($"cashierId={cashier}");
        if (f.PaymentMethodId is { } method) parts.Add($"paymentMethodId={method}");
        return "?" + string.Join('&', parts);
    }

    // ------------------------------------------------------------ orders, payments, kitchen, receipts

    public Task<PriceBreakdownDto> PreviewPriceAsync(PricePreviewRequest request) => api.PostAsync<PriceBreakdownDto>("api/orders/price-preview", request);
    public Task<IReadOnlyList<OrderSummaryDto>> GetOrdersAsync(string scope) => api.GetAsync<IReadOnlyList<OrderSummaryDto>>($"api/orders?scope={scope}");
    public Task<OrderDto> GetOrderAsync(Guid id) => api.GetAsync<OrderDto>($"api/orders/{id}");
    public Task<OrderDto> CreateOrderAsync(CreateOrderRequest request) => api.PostAsync<OrderDto>("api/orders", request);
    public Task<OrderDto> AddOrderLinesAsync(Guid id, AddOrderLinesRequest request) => api.PostAsync<OrderDto>($"api/orders/{id}/lines", request);
    public Task<OrderDto> SendToKitchenAsync(Guid id) => api.PostAsync<OrderDto>($"api/orders/{id}/send", new { });
    public Task<OrderDto> VoidLineAsync(Guid id, Guid lineId, ReasonRequest request) => api.PostAsync<OrderDto>($"api/orders/{id}/lines/{lineId}/void", request);
    public Task<OrderDto> CancelOrderAsync(Guid id, ReasonRequest request) => api.PostAsync<OrderDto>($"api/orders/{id}/cancel", request);
    public Task<OrderDto> VoidOrderAsync(Guid id, ReasonRequest request) => api.PostAsync<OrderDto>($"api/orders/{id}/void", request);
    public Task<OrderDto> SetDiscountAsync(Guid id, SetDiscountRequest request) => api.PutAsync<OrderDto>($"api/orders/{id}/discount", request);
    public Task<OrderDto> MarkServedAsync(Guid id) => api.PostAsync<OrderDto>($"api/orders/{id}/served", new { });
    public Task<OrderDto> TakePaymentAsync(Guid id, TakePaymentRequest request) => api.PostAsync<OrderDto>($"api/orders/{id}/payments", request);
    public Task<OrderDto> RefundAsync(Guid paymentId, RefundRequest request) => api.PostAsync<OrderDto>($"api/payments/{paymentId}/refunds", request);
    public Task<IReadOnlyList<ApproverDto>> GetApproversAsync(string permission) => api.GetAsync<IReadOnlyList<ApproverDto>>($"api/approvers?permission={Uri.EscapeDataString(permission)}");

    public Task<IReadOnlyList<KitchenTicketDto>> GetKitchenTicketsAsync(Guid? stationId) =>
        api.GetAsync<IReadOnlyList<KitchenTicketDto>>(stationId is null ? "api/kitchen/tickets" : $"api/kitchen/tickets?stationId={stationId}");
    public Task UpdateTicketAsync(Guid id, KitchenTicketStatus status) => api.PutAsync($"api/kitchen/tickets/{id}/status", new UpdateTicketStatusRequest(status));

    public Task<ReceiptDocumentDto> PrintReceiptAsync(Guid orderId, PrintReceiptRequest request) => api.PostAsync<ReceiptDocumentDto>($"api/orders/{orderId}/receipt/print", request);
    public Task<byte[]> GetReceiptPdfAsync(Guid orderId, PrintKind kind) => api.GetBytesAsync($"api/orders/{orderId}/receipt/pdf?kind={kind}");

    // ------------------------------------------------------------ inventory

    public Task<IReadOnlyList<InventoryItemDto>> GetInventoryItemsAsync() => api.GetAsync<IReadOnlyList<InventoryItemDto>>("api/inventory/items");
    public Task<InventoryItemDto> SaveInventoryItemAsync(Guid? id, SaveInventoryItemRequest request) =>
        id is null ? api.PostAsync<InventoryItemDto>("api/inventory/items", request) : api.PutAsync<InventoryItemDto>($"api/inventory/items/{id}", request);
    public Task<IReadOnlyList<StockLevelDto>> GetStockLevelsAsync() => api.GetAsync<IReadOnlyList<StockLevelDto>>("api/inventory/levels");
    public Task<IReadOnlyList<StockMovementDto>> GetStockMovementsAsync(Guid? itemId) =>
        api.GetAsync<IReadOnlyList<StockMovementDto>>(itemId is null ? "api/inventory/movements" : $"api/inventory/movements?itemId={itemId}");
    public Task<IReadOnlyList<StockReceiptDto>> GetStockReceiptsAsync() => api.GetAsync<IReadOnlyList<StockReceiptDto>>("api/inventory/receipts");
    public Task<StockReceiptDto> ReceiveStockAsync(CreateStockReceiptRequest request) => api.PostAsync<StockReceiptDto>("api/inventory/receipts", request);
    public Task<StockMovementDto> AdjustStockAsync(StockAdjustmentRequest request) => api.PostAsync<StockMovementDto>("api/inventory/adjustments", request);
    public Task<IReadOnlyList<StockCountDto>> GetStockCountsAsync() => api.GetAsync<IReadOnlyList<StockCountDto>>("api/inventory/counts");
    public Task<StockCountDto> CountStockAsync(CreateStockCountRequest request) => api.PostAsync<StockCountDto>("api/inventory/counts", request);
    public Task<IReadOnlyList<RecipeDto>> GetRecipesAsync() => api.GetAsync<IReadOnlyList<RecipeDto>>("api/inventory/recipes");
    public Task<RecipeDto> SaveRecipeAsync(RecipeDto recipe) => api.PutAsync<RecipeDto>("api/inventory/recipes", recipe);

    // ------------------------------------------------------------ deals and customers

    public Task<IReadOnlyList<DealDto>> GetDealsAsync() => api.GetAsync<IReadOnlyList<DealDto>>("api/deals");
    public Task<DealDto> GetDealAsync(Guid id) => api.GetAsync<DealDto>($"api/deals/{id}");
    public Task<DealDto> SaveDealAsync(Guid? id, SaveDealRequest request) =>
        id is null ? api.PostAsync<DealDto>("api/deals", request) : api.PutAsync<DealDto>($"api/deals/{id}", request);

    public Task<IReadOnlyList<CustomerDto>> SearchCustomersAsync(string? search) =>
        api.PostAsync<IReadOnlyList<CustomerDto>>("api/customers/search", new CustomerSearchRequest(search));
    public Task<CustomerDetailDto> GetCustomerAsync(Guid id) => api.GetAsync<CustomerDetailDto>($"api/customers/{id}");
    public Task<CustomerDto> SaveCustomerAsync(Guid? id, SaveCustomerRequest request) =>
        id is null ? api.PostAsync<CustomerDto>("api/customers", request) : api.PutAsync<CustomerDto>($"api/customers/{id}", request);
    public Task RemoveCustomerDataAsync(Guid id) => api.PostAsync($"api/customers/{id}/remove-personal-data", new { });
}
