using AuthService.Api.Common;
using AuthService.Api.Tokens;
using AuthService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthService.Api.Features.Applications;

/// <summary>Checks a product's client credentials and issues its short-lived service token (APP-05).</summary>
public sealed class ServiceTokenService(
    AuthDbContext db,
    IdentityTokenService tokens,
    IOptions<IdentityTokenOptions> tokenOptions,
    TimeProvider timeProvider,
    ILogger<ServiceTokenService> logger)
{
    /// <summary>A service token, or null when the credentials are not valid.</summary>
    public async Task<ServiceTokenResponse?> IssueAsync(string clientId, string clientSecret, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var secretHash = SecretToken.Hash(clientSecret);

        // One query for every outcome, so an unknown client and a wrong secret cost the same. A secret works until it expires:
        // the current one never does, and one being rotated out does at the end of its grace period.
        var application = await (
            from candidate in db.Applications
            join secret in db.ApplicationSecrets on candidate.Id equals secret.ApplicationId
            where candidate.ClientId == clientId
                && secret.SecretHash == secretHash
                && (secret.ExpiresAt == null || secret.ExpiresAt > now)
            select new { candidate.Id, candidate.ClientId })
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);

        if (application is null)
        {
            // Neither the client id nor the secret is logged: either could be a secret pasted into the wrong field.
            logger.LogWarning("A service token request was refused: the client credentials were not valid.");
            return null;
        }

        var token = tokens.CreateServiceToken(application.Id, application.ClientId, now);
        return new ServiceTokenResponse(token.Value, "Bearer", tokenOptions.Value.ServiceTokenMinutes * 60);
    }
}
