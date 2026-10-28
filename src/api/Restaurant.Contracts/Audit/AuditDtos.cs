namespace Restaurant.Contracts.Audit;

public record AuditLogDto(
    Guid Id,
    DateTimeOffset At,
    Guid? ActorUserId,
    string? ActorName,
    Guid? ApproverUserId,
    string Action,
    string EntityType,
    string? EntityId,
    string? Reason,
    string? BeforeJson,
    string? AfterJson,
    Guid? BranchId);
