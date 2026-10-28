using Restaurant.Domain.Common;

namespace Restaurant.Domain.Entities;

/// <summary>
/// A named set of permissions inside one tenant. The system roles (Owner, Manager, Cashier...) are
/// created with every tenant; owners can add their own. Code checks permissions, never role names.
/// </summary>
public class TenantRole : TenantEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsSystem { get; set; }

    /// <summary>"owner", "manager"... for system roles; null for custom ones.</summary>
    public string? SystemKey { get; set; }

    /// <summary>Every permission, including ones added in later releases. Only the Owner role has it.</summary>
    public bool FullAccess { get; set; }

    public List<RolePermission> Permissions { get; set; } = [];
}

public class RolePermission : ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid RoleId { get; set; }
    public string Permission { get; set; } = string.Empty;
}
