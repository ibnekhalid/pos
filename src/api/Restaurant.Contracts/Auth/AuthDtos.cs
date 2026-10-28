namespace Restaurant.Contracts.Auth;

/// <summary>Self sign-up: creates the restaurant (tenant), its first branch and register, and the owner.</summary>
public record RegisterOwnerRequest(
    string FullName,
    string Email,
    string? Phone,
    string Password,
    string RestaurantName,
    string? BranchName,
    string? BranchAddress,
    string? City);

/// <param name="Login">Email address or mobile number.</param>
/// <param name="RememberMe">A long, sliding session when true; a 12-hour session when false.</param>
public record LoginRequest(string Login, string Password, bool RememberMe = true);

public record SelectTenantRequest(Guid TenantId, bool RememberMe = true);

public record RefreshTokenRequest(string RefreshToken);

public record ForgotPasswordRequest(string Email);

public record ResetPasswordRequest(string Email, string Token, string NewPassword);

public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>Accepts a staff invite. A new user sets their password; an existing user confirms theirs.</summary>
public record AcceptInviteRequest(string Code, string Password);

/// <summary>Counter sign-in: a staff PIN, only from a device the restaurant registered.</summary>
public record PinLoginRequest(string DeviceToken, Guid MembershipId, string Pin);

public record SetPinRequest(string CurrentPassword, string Pin);

public record TenantMembershipDto(Guid TenantId, string TenantName, Guid MembershipId, IReadOnlyList<string> RoleNames, bool IsOwner);

/// <param name="RequiresTenantSelection">
/// The user belongs to several restaurants: AccessToken only allows POST /api/auth/select-tenant.
/// </param>
/// <param name="DefaultBranchId">Send it as X-Branch-Id (PIN sign-in: the device's branch).</param>
public record AuthResultDto(
    bool RequiresTenantSelection,
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string? RefreshToken,
    DateTimeOffset? RefreshTokenExpiresAt,
    Guid? TenantId,
    Guid? DefaultBranchId,
    IReadOnlyList<TenantMembershipDto> Tenants);

public record DeviceStaffDto(Guid MembershipId, string DisplayName);

public record BranchSummaryDto(Guid Id, string Name);

/// <summary>Who is signed in and what they may do. The clients use it to build navigation; the API enforces it.</summary>
public record MeDto(
    Guid UserId,
    string FullName,
    string? Email,
    string? Phone,
    bool IsPlatformAdmin,
    Guid? TenantId,
    string? TenantName,
    Guid? MembershipId,
    bool IsOwner,
    bool HasPin,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<BranchSummaryDto> Branches,
    IReadOnlyList<TenantMembershipDto> Tenants);
