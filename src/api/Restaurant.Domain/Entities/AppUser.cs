using Microsoft.AspNetCore.Identity;

namespace Restaurant.Domain.Entities;

/// <summary>
/// A person who can sign in (ASP.NET Core Identity). Not owned by a tenant: one user can work for
/// several restaurants, each through its own StaffMembership. UserName is the normalised email or
/// mobile number they sign in with.
/// </summary>
public class AppUser : IdentityUser<Guid>
{
    public AppUser()
    {
        Id = Guid.CreateVersion7();
        SecurityStamp = Guid.NewGuid().ToString();
    }

    public string FullName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
