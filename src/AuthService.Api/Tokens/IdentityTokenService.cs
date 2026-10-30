using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AuthService.Api.Tokens;

public sealed record AccessToken(string Value, DateTimeOffset ExpiresAt);

/// <summary>A new refresh token: the secret to hand to the client, and the row to store (which holds only a hash).</summary>
public sealed record IssuedRefreshToken(string Value, RefreshToken Entity);

/// <summary>Creates the tokens a person receives at login.</summary>
public sealed class IdentityTokenService(IOptions<IdentityTokenOptions> options, SigningKeyProvider signingKey)
{
    private static readonly JsonWebTokenHandler Handler = new();

    /// <summary>
    /// An identity access token carries the user id (<c>sub</c>) and nothing else: no tenant, no permissions and no
    /// audience. A product token with those claims is issued later, at token exchange (TOK-01).
    /// </summary>
    public AccessToken CreateAccessToken(Guid userId, DateTimeOffset now)
    {
        var settings = options.Value;
        var expiresAt = now.AddMinutes(settings.AccessTokenMinutes);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            ]),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = new SigningCredentials(signingKey.Key, SecurityAlgorithms.EcdsaSha256),
        };

        return new AccessToken(Handler.CreateToken(descriptor), expiresAt);
    }

    /// <summary>Starts a new refresh-token family, as happens at login.</summary>
    public IssuedRefreshToken CreateRefreshToken(Guid userId, DateTimeOffset now)
    {
        var value = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

        var entity = new RefreshToken
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            FamilyId = Guid.CreateVersion7(),
            TokenHash = Hash(value),
            CreatedAt = now,
            ExpiresAt = now.AddDays(options.Value.RefreshTokenDays),
        };

        return new IssuedRefreshToken(value, entity);
    }

    /// <summary>The form in which a refresh token is stored and looked up.</summary>
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
