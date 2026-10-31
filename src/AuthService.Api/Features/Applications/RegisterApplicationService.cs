using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AuthService.Api.Common;
using AuthService.Api.Features.Audit;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Configurations;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace AuthService.Api.Features.Applications;

/// <summary>A registration request that passed validation, with the key normalised.</summary>
public sealed record ValidApplication(string Key, string Name, string JwtAudience);

public abstract record RegisterApplicationResult
{
    public sealed record Created(RegisterApplicationResponse Response) : RegisterApplicationResult;

    public sealed record KeyTaken : RegisterApplicationResult;

    public sealed record AudienceTaken : RegisterApplicationResult;
}

public static partial class ApplicationValidator
{
    public const int MaxKeyLength = 63;
    public const int MaxNameLength = 200;
    public const int MaxAudienceLength = 200;

    public static bool TryValidate(
        RegisterApplicationRequest request,
        out ValidApplication? valid,
        out Dictionary<string, string[]> errors)
    {
        errors = [];
        valid = null;

        // Keys are case-insensitive: "WhatsApp-SaaS" and "whatsapp-saas" are the same product.
        var key = request.Key?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(key))
        {
            errors[nameof(RegisterApplicationRequest.Key)] = ["Key is required."];
        }
        else if (!KeyPattern().IsMatch(key))
        {
            errors[nameof(RegisterApplicationRequest.Key)] =
                [$"Key must be 1 to {MaxKeyLength} lowercase letters, digits or hyphens, and start and end with a letter or digit."];
        }

        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            errors[nameof(RegisterApplicationRequest.Name)] = ["Name is required."];
        }
        else if (name.Length > MaxNameLength)
        {
            errors[nameof(RegisterApplicationRequest.Name)] = [$"Name must be at most {MaxNameLength} characters."];
        }

        var audience = request.JwtAudience?.Trim();
        if (string.IsNullOrEmpty(audience))
        {
            errors[nameof(RegisterApplicationRequest.JwtAudience)] = ["JWT audience is required."];
        }
        else if (audience.Length > MaxAudienceLength || !AudiencePattern().IsMatch(audience))
        {
            errors[nameof(RegisterApplicationRequest.JwtAudience)] =
                [$"JWT audience must be at most {MaxAudienceLength} characters: letters, digits and . _ : / - only, no spaces."];
        }

        if (errors.Count > 0)
        {
            return false;
        }

        valid = new ValidApplication(key!, name!, audience!);
        return true;
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex("^[A-Za-z0-9._:/-]+$")]
    private static partial Regex AudiencePattern();
}

/// <summary>APP-01 and the credential half of APP-05: registers a product and issues its client credentials.</summary>
public sealed class RegisterApplicationService(AuthDbContext db, AuditLog audit, TimeProvider timeProvider)
{
    public async Task<RegisterApplicationResult> RegisterAsync(Guid adminId, ValidApplication input, CancellationToken cancellationToken)
    {
        // Friendly fast path. The unique indexes are what actually guarantee it (two simultaneous registrations).
        if (await db.Applications.AnyAsync(application => application.Key == input.Key, cancellationToken))
        {
            return new RegisterApplicationResult.KeyTaken();
        }

        if (await db.Applications.AnyAsync(application => application.JwtAudience == input.JwtAudience, cancellationToken))
        {
            return new RegisterApplicationResult.AudienceTaken();
        }

        var now = timeProvider.GetUtcNow();
        var clientSecret = SecretToken.NewValue();

        var application = new Application
        {
            Id = Guid.CreateVersion7(),
            Key = input.Key,
            Name = input.Name,
            JwtAudience = input.JwtAudience,
            ClientId = "cl_" + Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(16)),

            // Inactive until the permission catalog is registered (APP-02).
            IsActive = false,
            CreatedAt = now,
        };

        db.Applications.Add(application);
        db.ApplicationSecrets.Add(new ApplicationSecret
        {
            Id = Guid.CreateVersion7(),
            ApplicationId = application.Id,
            SecretHash = SecretToken.Hash(clientSecret),
            CreatedAt = now,
        });

        // The product's key is a safe, readable detail; the secret never goes near the audit log.
        audit.Record(AuditActions.ApplicationRegistered, AuditActor.User(adminId), detail: application.Key);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (BrokeUniqueRule(exception, ApplicationConfiguration.UniqueKeyIndexName))
        {
            return new RegisterApplicationResult.KeyTaken();
        }
        catch (DbUpdateException exception) when (BrokeUniqueRule(exception, ApplicationConfiguration.UniqueAudienceIndexName))
        {
            return new RegisterApplicationResult.AudienceTaken();
        }

        return new RegisterApplicationResult.Created(new RegisterApplicationResponse(
            application.Id, application.Key, application.Name, application.JwtAudience, application.IsActive,
            application.ClientId, clientSecret, now));
    }

    private static bool BrokeUniqueRule(DbUpdateException exception, string indexName) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } violation
        && violation.ConstraintName == indexName;
}
