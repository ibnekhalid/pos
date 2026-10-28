using Restaurant.Domain.Common;

namespace Restaurant.Domain.Entities;

/// <summary>
/// Append-only record of an important action (PLAN.md section 15). There is no update or delete
/// path in the application. Never put passwords, PINs, tokens or unnecessary personal data in it.
/// </summary>
public class AuditLog : Entity
{
    public Guid? TenantId { get; set; }
    public Guid? BranchId { get; set; }
    public Guid? ActorUserId { get; set; }

    /// <summary>The manager who approved an action the actor couldn't do alone.</summary>
    public Guid? ApproverUserId { get; set; }

    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string? EntityId { get; set; }
    public string? Reason { get; set; }
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
    public string? IpAddress { get; set; }
}
