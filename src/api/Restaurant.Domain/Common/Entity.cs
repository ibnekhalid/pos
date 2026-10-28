namespace Restaurant.Domain.Common;

/// <summary>Base for every persisted entity. Ids are UUIDv7 so they sort by creation time in indexes.</summary>
public abstract class Entity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}

/// <summary>
/// Belongs to exactly one tenant (restaurant business). TenantId is stamped from the resolved tenant
/// context when the row is inserted — never taken from client input.
/// </summary>
public interface ITenantOwned
{
    Guid TenantId { get; set; }
}

/// <summary>Belongs to one branch of a tenant (sessions, orders, stock, tables...).</summary>
public interface IBranchOwned : ITenantOwned
{
    Guid BranchId { get; set; }
}

public abstract class TenantEntity : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
}

public abstract class BranchEntity : TenantEntity, IBranchOwned
{
    public Guid BranchId { get; set; }
}
