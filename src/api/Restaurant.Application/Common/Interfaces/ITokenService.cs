namespace Restaurant.Application.Common.Interfaces;

public record AccessToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>Signs JWT access tokens (implemented in the API, which owns the signing key).</summary>
public interface ITokenService
{
    /// <param name="tenantId">Null for a platform administrator's session or a pending tenant selection.</param>
    /// <param name="pendingSelection">The token only allows choosing a restaurant.</param>
    AccessToken CreateAccessToken(Guid userId, Guid? tenantId, Guid? membershipId, bool pendingSelection, TimeSpan lifetime);
}
