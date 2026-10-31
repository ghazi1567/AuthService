using System.Security.Claims;
using AuthService.Api.Common;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AuthService.Api.Tokens;

public sealed record AccessToken(string Value, DateTimeOffset ExpiresAt);

/// <summary>A new refresh token: the secret to hand to the client, and the row to store (which holds only a hash).</summary>
public sealed record IssuedRefreshToken(string Value, RefreshToken Entity);

/// <summary>Creates the tokens a person receives at login.</summary>
public sealed class IdentityTokenService(IOptions<IdentityTokenOptions> options, SigningKeyService signingKeys)
{
    /// <summary>The <c>aud</c> of service tokens. No product may use it as its own audience (see <c>ApplicationValidator</c>).</summary>
    public const string ServiceAudience = "auth-service";

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
            SigningCredentials = new SigningCredentials(signingKeys.CurrentSigningKey, SecurityAlgorithms.EcdsaSha256),
        };

        return new AccessToken(Handler.CreateToken(descriptor), expiresAt);
    }

    /// <summary>
    /// A product's service token (APP-05). It names the product (<c>app_id</c>, <c>client_id</c>) and carries the Auth Service's own
    /// audience, so it is refused on every identity endpoint, and its <c>sub</c> is not a user id, so it can never be mistaken for
    /// a person. It opens only the endpoints that take the service-token policy, which act on that product's own data.
    /// </summary>
    public AccessToken CreateServiceToken(Guid applicationId, string clientId, DateTimeOffset now)
    {
        var expiresAt = now.AddMinutes(options.Value.ServiceTokenMinutes);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = options.Value.Issuer,
            Audience = ServiceAudience,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, "app:" + applicationId),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                new Claim(ServiceClaims.TokenUse, ServiceClaims.ServiceTokenUse),
                new Claim(ServiceClaims.ApplicationId, applicationId.ToString()),
                new Claim(ServiceClaims.ClientId, clientId),
            ]),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = new SigningCredentials(signingKeys.CurrentSigningKey, SecurityAlgorithms.EcdsaSha256),
        };

        return new AccessToken(Handler.CreateToken(descriptor), expiresAt);
    }

    /// <summary>
    /// A product token (TOK-01): for one person, in one tenant, for one product. <c>aud</c> is the product's audience and
    /// <c>tid</c> the tenant; <c>permissions</c> lists <c>key:Level</c> for that product only, such as
    /// <c>campaign.create:Full</c>. Nothing from another product or another tenant is ever in it.
    /// </summary>
    public AccessToken CreateProductToken(
        Guid userId, Guid tenantId, string audience, IReadOnlyCollection<string> permissions, DateTimeOffset now)
    {
        var expiresAt = now.AddMinutes(options.Value.ProductTokenMinutes);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = options.Value.Issuer,
            Audience = audience,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            ]),
            Claims = new Dictionary<string, object>
            {
                [ProductClaims.TenantId] = tenantId.ToString(),
                [ProductClaims.Permissions] = permissions.ToArray(),
            },
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = new SigningCredentials(signingKeys.CurrentSigningKey, SecurityAlgorithms.EcdsaSha256),
        };

        return new AccessToken(Handler.CreateToken(descriptor), expiresAt);
    }

    /// <summary>Starts a new refresh-token family, as happens at login.</summary>
    public IssuedRefreshToken CreateRefreshToken(Guid userId, DateTimeOffset now) =>
        CreateRefreshToken(userId, Guid.CreateVersion7(), now);

    /// <summary>Issues the next token in an existing family, as happens when a refresh token is rotated.</summary>
    public IssuedRefreshToken CreateRefreshToken(Guid userId, Guid familyId, DateTimeOffset now)
    {
        var value = SecretToken.NewValue();

        var entity = new RefreshToken
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            FamilyId = familyId,
            TokenHash = Hash(value),
            CreatedAt = now,
            ExpiresAt = now.AddDays(options.Value.RefreshTokenDays),
        };

        return new IssuedRefreshToken(value, entity);
    }

    /// <summary>The form in which a refresh token is stored and looked up.</summary>
    public static string Hash(string token) => SecretToken.Hash(token);
}
