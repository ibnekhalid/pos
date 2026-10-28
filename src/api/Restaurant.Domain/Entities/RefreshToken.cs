using Restaurant.Domain.Common;

namespace Restaurant.Domain.Entities;

/// <summary>
/// A refresh token (only its SHA-256 hash is stored). Rotated on every use; presenting an
/// already-rotated token revokes the user's sessions (token theft).
/// </summary>
public class RefreshToken : Entity
{
    public Guid UserId { get; set; }

    /// <summary>Null for a platform administrator's tenant-less session.</summary>
    public Guid? TenantId { get; set; }

    public Guid? MembershipId { get; set; }

    /// <summary>Set for PIN sessions started on a registered counter device.</summary>
    public Guid? DeviceId { get; set; }

    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? ReplacedByTokenHash { get; set; }
    public bool IsPersistent { get; set; }
    public string? CreatedByIp { get; set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
}
