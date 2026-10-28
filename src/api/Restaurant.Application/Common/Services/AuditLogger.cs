using System.Text.Json;
using System.Text.Json.Serialization;
using Restaurant.Application.Common.Interfaces;
using Restaurant.Domain.Entities;

namespace Restaurant.Application.Common.Services;

public static class AuditActions
{
    public const string TenantRegistered = "tenant.registered";
    public const string TenantUpdated = "tenant.updated";
    public const string TenantSuspended = "tenant.suspended";
    public const string TenantActivated = "tenant.activated";
    public const string BranchCreated = "branch.created";
    public const string BranchUpdated = "branch.updated";
    public const string BranchSettingsUpdated = "branch.settings_updated";
    public const string RegisterSaved = "register.saved";
    public const string TaxRateSaved = "tax_rate.saved";
    public const string PaymentMethodSaved = "payment_method.saved";
    public const string RoleCreated = "role.created";
    public const string RoleUpdated = "role.updated";
    public const string RoleDeleted = "role.deleted";
    public const string StaffCreated = "staff.created";
    public const string StaffUpdated = "staff.updated";
    public const string StaffInviteIssued = "staff.invite_issued";
    public const string StaffInviteAccepted = "staff.invite_accepted";
    public const string StaffPinSet = "staff.pin_set";
    public const string OwnershipTransferred = "ownership.transferred";
    public const string DeviceRegistered = "device.registered";
    public const string DeviceRevoked = "device.revoked";
    public const string DeviceUpdated = "device.updated";
    public const string BrandingUpdated = "branding.updated";
    public const string LogoUpdated = "branding.logo_updated";
    public const string Login = "auth.login";
    public const string PinLogin = "auth.pin_login";
    public const string PasswordChanged = "auth.password_changed";
    public const string PasswordReset = "auth.password_reset";
    public const string MenuItemSaved = "menu.item_saved";
    public const string MenuPriceOverride = "menu.branch_price";
    public const string SessionStarted = "session.started";
    public const string SessionClosed = "session.closed";
    public const string SessionReopened = "session.reopened";
    public const string CashMovement = "cash.movement";
    public const string OrderDiscount = "order.discount";
    public const string OrderLineVoided = "order.line_voided";
    public const string OrderCancelled = "order.cancelled";
    public const string OrderVoided = "order.voided";
    public const string PaymentRefunded = "payment.refunded";
    public const string InventoryItemSaved = "inventory.item_saved";
    public const string StockReceived = "inventory.received";
    public const string StockWastage = "inventory.wastage";
    public const string StockAdjusted = "inventory.adjusted";
    public const string StockCounted = "inventory.counted";
    public const string RecipeSaved = "inventory.recipe_saved";
    public const string DealSaved = "deal.saved";
    public const string CustomerSaved = "customer.saved";
    public const string CustomerAnonymized = "customer.anonymized";
}

/// <summary>
/// Adds an AuditLog row to the current unit of work, so it is saved in the same transaction as the
/// change it describes (or not at all). Before/after snapshots are DTOs — never secrets.
/// </summary>
public class AuditLogger(IApplicationDbContext db, ITenantContext tenant)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public void Record(
        string action,
        string entityType,
        object? entityId,
        object? before = null,
        object? after = null,
        string? reason = null,
        Guid? approverUserId = null,
        Guid? tenantId = null,
        Guid? branchId = null,
        Guid? actorUserId = null)
    {
        db.AuditLogs.Add(new AuditLog
        {
            TenantId = tenantId ?? tenant.TenantId,
            BranchId = branchId ?? tenant.BranchId,
            ActorUserId = actorUserId ?? tenant.UserId,
            ApproverUserId = approverUserId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId?.ToString(),
            Reason = reason,
            BeforeJson = before is null ? null : JsonSerializer.Serialize(before, Json),
            AfterJson = after is null ? null : JsonSerializer.Serialize(after, Json),
            IpAddress = tenant.RemoteIp
        });
    }
}
