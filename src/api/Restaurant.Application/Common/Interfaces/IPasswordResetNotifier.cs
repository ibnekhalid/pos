using Restaurant.Domain.Entities;

namespace Restaurant.Application.Common.Interfaces;

/// <summary>
/// Delivers a password-reset token. No email/SMS provider is chosen yet: the default implementation
/// only logs that a reset was requested (and the token itself in Development). Until a provider is
/// added, owners reset a staff member's access by issuing a new invite code.
/// </summary>
public interface IPasswordResetNotifier
{
    Task SendPasswordResetAsync(AppUser user, string token, CancellationToken cancellationToken = default);
}
