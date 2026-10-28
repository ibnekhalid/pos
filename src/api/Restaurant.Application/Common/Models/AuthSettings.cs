namespace Restaurant.Application.Common.Models;

/// <summary>Session lifetimes and lockout rules ("Auth" in appsettings).</summary>
public class AuthSettings
{
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 30;
    public int SessionRefreshTokenHours { get; set; } = 12;
    public int PendingSelectionMinutes { get; set; } = 10;
    public int PinSessionHours { get; set; } = 12;
    public int InviteDays { get; set; } = 7;
    public int PinMaxAttempts { get; set; } = 5;
    public int PinLockMinutes { get; set; } = 5;

    /// <summary>
    /// A just-rotated refresh token presented again within this window is rejected without treating
    /// it as theft (two tabs refreshing at the same moment).
    /// </summary>
    public int RefreshReuseGraceSeconds { get; set; } = 30;
}
