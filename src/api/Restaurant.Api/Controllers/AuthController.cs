using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Restaurant.Application.Features.Auth;
using Restaurant.Contracts.Auth;

namespace Restaurant.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(AuthService auth) : ControllerBase
{
    /// <summary>Header carrying the desktop app's device token (kept out of URLs and logs).</summary>
    public const string DeviceTokenHeader = "X-Device-Token";

    [HttpPost("register-owner")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public Task<AuthResultDto> RegisterOwner(RegisterOwnerRequest request, CancellationToken ct) => auth.RegisterOwnerAsync(request, ct);

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public Task<AuthResultDto> Login(LoginRequest request, CancellationToken ct) => auth.LoginAsync(request, ct);

    /// <summary>After a login that returned RequiresTenantSelection, or to switch restaurant.</summary>
    [HttpPost("select-tenant")]
    [Authorize]
    public Task<AuthResultDto> SelectTenant(SelectTenantRequest request, CancellationToken ct) => auth.SelectTenantAsync(request, ct);

    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public Task<AuthResultDto> Refresh(RefreshTokenRequest request, CancellationToken ct) => auth.RefreshAsync(request, ct);

    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout(RefreshTokenRequest request, CancellationToken ct)
    {
        await auth.LogoutAsync(request, ct);
        return NoContent();
    }

    [HttpPost("forgot-password")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request, CancellationToken ct)
    {
        await auth.ForgotPasswordAsync(request, ct);
        return NoContent();
    }

    [HttpPost("reset-password")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request, CancellationToken ct)
    {
        await auth.ResetPasswordAsync(request, ct);
        return NoContent();
    }

    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken ct)
    {
        await auth.ChangePasswordAsync(request, ct);
        return NoContent();
    }

    [HttpPost("accept-invite")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public Task<AuthResultDto> AcceptInvite(AcceptInviteRequest request, CancellationToken ct) => auth.AcceptInviteAsync(request, ct);

    /// <summary>Who can sign in with a PIN on this registered device.</summary>
    [HttpGet("device-staff")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public Task<IReadOnlyList<DeviceStaffDto>> DeviceStaff([FromHeader(Name = DeviceTokenHeader)] string deviceToken, CancellationToken ct) =>
        auth.GetDeviceStaffAsync(deviceToken, ct);

    [HttpPost("pin-login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public Task<AuthResultDto> PinLogin(PinLoginRequest request, CancellationToken ct) => auth.PinLoginAsync(request, ct);
}

[ApiController]
[Route("api/me")]
[Authorize]
public class MeController(AuthService auth) : ControllerBase
{
    /// <summary>The signed-in user, the current restaurant, permissions and usable branches.</summary>
    [HttpGet]
    public Task<MeDto> Get(CancellationToken ct) => auth.GetMeAsync(ct);

    /// <summary>Sets the caller's counter PIN for the current restaurant.</summary>
    [HttpPut("pin")]
    public async Task<IActionResult> SetPin(SetPinRequest request, CancellationToken ct)
    {
        await auth.SetPinAsync(request, ct);
        return NoContent();
    }
}
