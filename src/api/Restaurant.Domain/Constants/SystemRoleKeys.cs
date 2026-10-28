namespace Restaurant.Domain.Constants;

public static class SystemRoleKeys
{
    public const string Owner = "owner";
    public const string Manager = "manager";
    public const string Cashier = "cashier";
    public const string Waiter = "waiter";
    public const string Kitchen = "kitchen";
    public const string Inventory = "inventory";

    /// <summary>The single ASP.NET Identity role. Platform staff only — never a restaurant member.</summary>
    public const string PlatformAdminRole = "PlatformAdmin";
}
