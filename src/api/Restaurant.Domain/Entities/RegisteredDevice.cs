using Restaurant.Domain.Common;

namespace Restaurant.Domain.Entities;

/// <summary>
/// A counter PC the restaurant trusts for PIN sign-in. The desktop app holds the device token
/// (only its hash is stored here); revoking the device stops PIN sign-in on it immediately.
/// </summary>
public class RegisteredDevice : BranchEntity
{
    public Guid? RegisterId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
