using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AuthService.Api.Common;
using AuthService.Api.Features.Audit;
using AuthService.Api.Tokens;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Configurations;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace AuthService.Api.Features.Applications;

/// <summary>
/// A registration request that passed validation, with the key normalised. <see cref="Permissions"/> and <see cref="LaunchUrl"/> are
/// null when the request did not send them; <see cref="Activate"/> is only true together with at least one permission.
/// </summary>
public sealed record ValidApplication(
    string Key,
    string Name,
    string JwtAudience,
    string? LaunchUrl = null,
    IReadOnlyList<ValidPermission>? Permissions = null,
    bool Activate = false);

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

        if (!TryValidateName(request.Name, out var name, out var nameError))
        {
            errors[nameof(RegisterApplicationRequest.Name)] = [nameError];
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
        else if (string.Equals(audience, IdentityTokenService.ServiceAudience, StringComparison.OrdinalIgnoreCase))
        {
            // Service tokens carry this audience; a product using it could be handed one as if it were its own token.
            errors[nameof(RegisterApplicationRequest.JwtAudience)] = [$"JWT audience '{IdentityTokenService.ServiceAudience}' is reserved."];
        }

        string? launchUrl = null;
        if (!string.IsNullOrWhiteSpace(request.LaunchUrl) && !LaunchUrlValidator.TryValidate(request.LaunchUrl, out launchUrl, out var launchError))
        {
            errors[nameof(RegisterApplicationRequest.LaunchUrl)] = [launchError];
        }

        // An empty list is the same as none, so a form that sends an empty catalogue registers only the product.
        List<ValidPermission>? permissions = null;
        if (request.Permissions is { Count: > 0 })
        {
            if (PermissionCatalogValidator.TryValidate(new RegisterPermissionsRequest(request.Permissions), out var checkedPermissions, out var permissionErrors))
            {
                permissions = checkedPermissions;
            }
            else
            {
                foreach (var (field, messages) in permissionErrors)
                {
                    errors[field] = messages;
                }
            }
        }

        if (request.Activate == true && request.Permissions is not { Count: > 0 })
        {
            errors[nameof(RegisterApplicationRequest.Activate)] = ["Activate needs at least one permission, so a tenant has something to grant."];
        }

        if (errors.Count > 0)
        {
            return false;
        }

        valid = new ValidApplication(key!, name, audience!, launchUrl, permissions, request.Activate == true);
        return true;
    }

    /// <summary>The rules for a product's display name, used when it is registered and when it is renamed.</summary>
    public static bool TryValidateName(string? text, out string name, out string error)
    {
        name = text?.Trim() ?? "";
        error = "";
        if (name.Length == 0)
        {
            error = "Name is required.";
            return false;
        }

        if (name.Length > MaxNameLength)
        {
            error = $"Name must be at most {MaxNameLength} characters.";
            return false;
        }

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

            // Inactive until the permission catalog is registered (APP-02), unless this same request sent one and asked to activate.
            IsActive = input.Activate,
            LaunchUrl = input.LaunchUrl,
            CreatedAt = now,
        };

        db.Applications.Add(application);
        foreach (var permission in input.Permissions ?? [])
        {
            db.Permissions.Add(new Permission
            {
                Id = Guid.CreateVersion7(),
                ApplicationId = application.Id,
                Key = permission.Key,
                Label = permission.Label,
                Description = permission.Description,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        db.ApplicationSecrets.Add(new ApplicationSecret
        {
            Id = Guid.CreateVersion7(),
            ApplicationId = application.Id,
            SecretHash = SecretToken.Hash(clientSecret),
            CreatedAt = now,
        });

        // The product's key is a safe, readable detail; the secret never goes near the audit log.
        audit.Record(AuditActions.ApplicationRegistered, AuditActor.User(adminId), detail: application.Key);

        // The same entries the separate calls write, so the audit log reads the same however the product was set up.
        if (input.Permissions is { Count: > 0 })
        {
            audit.Record(AuditActions.PermissionsRegistered, AuditActor.User(adminId), detail: application.Key);
        }

        if (input.LaunchUrl is not null)
        {
            audit.Record(AuditActions.ApplicationLaunchUrlSet, AuditActor.User(adminId), detail: application.Key);
        }

        if (input.Activate)
        {
            audit.Record(AuditActions.ApplicationActivated, AuditActor.User(adminId), detail: application.Key);
        }

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
            application.ClientId, clientSecret, now, application.LaunchUrl, input.Permissions?.Count ?? 0));
    }

    private static bool BrokeUniqueRule(DbUpdateException exception, string indexName) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } violation
        && violation.ConstraintName == indexName;
}
