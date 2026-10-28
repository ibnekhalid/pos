using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Restaurant.Application.Common.Interfaces;

namespace Restaurant.Api.Auth;

public class JwtSettings
{
    public string Issuer { get; set; } = "RestaurantPos";
    public string Audience { get; set; } = "RestaurantPos.Clients";
    public string SigningKey { get; set; } = string.Empty;
}

/// <summary>Token claim names. Permissions are deliberately not in the token: they are re-read per request.</summary>
public static class AppClaims
{
    public const string UserId = "sub";
    public const string TenantId = "tid";
    public const string MembershipId = "mid";

    /// <summary>"1": the user still has to choose a restaurant; only selection endpoints accept the token.</summary>
    public const string PendingSelection = "sel";
}

public class JwtTokenService(IOptions<JwtSettings> settings) : ITokenService
{
    private readonly JsonWebTokenHandler _handler = new();

    public AccessToken CreateAccessToken(Guid userId, Guid? tenantId, Guid? membershipId, bool pendingSelection, TimeSpan lifetime)
    {
        var jwt = settings.Value;
        var claims = new List<Claim>
        {
            new(AppClaims.UserId, userId.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };
        if (tenantId is not null && membershipId is not null)
        {
            claims.Add(new(AppClaims.TenantId, tenantId.Value.ToString()));
            claims.Add(new(AppClaims.MembershipId, membershipId.Value.ToString()));
        }
        if (pendingSelection)
        {
            claims.Add(new(AppClaims.PendingSelection, "1"));
        }

        var expiresAt = DateTimeOffset.UtcNow.Add(lifetime);
        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            Subject = new ClaimsIdentity(claims),
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)), SecurityAlgorithms.HmacSha256)
        });
        return new AccessToken(token, expiresAt);
    }
}
