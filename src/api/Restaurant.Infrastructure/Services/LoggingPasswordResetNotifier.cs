using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Restaurant.Application.Common.Interfaces;
using Restaurant.Domain.Entities;

namespace Restaurant.Infrastructure.Services;

public class PasswordResetSettings
{
    /// <summary>Write the reset token to the log. Development only — never enable in production.</summary>
    public bool LogTokens { get; set; }
}

/// <summary>
/// Placeholder until an email/SMS provider is chosen: records that a reset was requested. Owners can
/// already reset a staff member's access by issuing a new invite code.
/// </summary>
public class LoggingPasswordResetNotifier(IOptions<PasswordResetSettings> settings, ILogger<LoggingPasswordResetNotifier> logger) : IPasswordResetNotifier
{
    public Task SendPasswordResetAsync(AppUser user, string token, CancellationToken cancellationToken = default)
    {
        if (settings.Value.LogTokens)
        {
            logger.LogWarning("DEVELOPMENT password reset token for user {UserId}: {Token}", user.Id, token);
        }
        else
        {
            logger.LogInformation("Password reset requested for user {UserId}; no delivery channel is configured", user.Id);
        }
        return Task.CompletedTask;
    }
}
