using System.Text.Json;
using System.Text.RegularExpressions;
using AuthService.Api.Features.Applications;
using AuthService.Api.Features.Audit;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.Entitlements;

/// <summary>A limit that passed validation, with its key normalised.</summary>
public sealed record ValidLimit(string Key, string Label, string? Description, string? Unit, long? DefaultValue);

public abstract record RegisterLimitsResult
{
    public sealed record Registered(RegisterLimitsResponse Response) : RegisterLimitsResult;

    public sealed record ApplicationNotFound : RegisterLimitsResult;

    /// <summary>The request would take the product past <see cref="LimitDefinitionValidator.MaxLimitsPerProduct"/> limits. Nothing was saved.</summary>
    public sealed record TooMany(int Total) : RegisterLimitsResult;
}

public static partial class LimitDefinitionValidator
{
    public const int MaxLimitsPerProduct = 100;
    public const int MaxKeyLength = 100;
    public const int MaxLabelLength = 200;
    public const int MaxDescriptionLength = 1000;
    public const int MaxUnitLength = 50;

    /// <summary>The unit that marks an on/off feature: its values can only be 0 (off) or 1 (on).</summary>
    public const string FlagUnit = "flag";

    public static bool TryValidate(RegisterLimitsRequest request, out List<ValidLimit> valid, out Dictionary<string, string[]> errors)
    {
        errors = [];
        valid = [];

        if (request.Limits is not { Count: > 0 })
        {
            errors[nameof(RegisterLimitsRequest.Limits)] = ["At least one limit is required."];
            return false;
        }

        if (request.Limits.Count > MaxLimitsPerProduct)
        {
            errors[nameof(RegisterLimitsRequest.Limits)] = [$"At most {MaxLimitsPerProduct} limits per request."];
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < request.Limits.Count; index++)
        {
            var input = request.Limits[index];
            var field = $"{nameof(RegisterLimitsRequest.Limits)}[{index}]";
            var ok = true;

            // Keys are not case-sensitive: "Contacts.Reachable" and "contacts.reachable" are the same limit. Spaces inside a key are an error.
            var key = input?.Key?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength || !KeyPattern().IsMatch(key))
            {
                errors[$"{field}.Key"] =
                    [$"Key must be 1 to {MaxKeyLength} lowercase letters, digits and . _ - characters, such as contacts.reachable."];
                ok = false;
            }
            else if (!seen.Add(key))
            {
                errors[$"{field}.Key"] = [$"Key '{key}' appears more than once in this request."];
                ok = false;
            }

            var label = input?.Label?.Trim();
            if (string.IsNullOrEmpty(label) || label.Length > MaxLabelLength)
            {
                errors[$"{field}.Label"] = [$"Label is required and must be at most {MaxLabelLength} characters."];
                ok = false;
            }

            var description = string.IsNullOrWhiteSpace(input?.Description) ? null : input.Description.Trim();
            if (description is { Length: > MaxDescriptionLength })
            {
                errors[$"{field}.Description"] = [$"Description must be at most {MaxDescriptionLength} characters."];
                ok = false;
            }

            var unit = string.IsNullOrWhiteSpace(input?.Unit) ? null : input.Unit.Trim();
            if (unit is { Length: > MaxUnitLength })
            {
                errors[$"{field}.Unit"] = [$"Unit must be at most {MaxUnitLength} characters."];
                ok = false;
            }

            long? defaultValue = null;
            string? valueError = DefaultValueRequired;
            if (input is null || !TryReadValue(input.DefaultValue, out defaultValue, out valueError))
            {
                errors[$"{field}.DefaultValue"] = [valueError ?? DefaultValueRequired];
                ok = false;
            }
            else if (IsFlag(unit) && defaultValue is not (0 or 1))
            {
                errors[$"{field}.DefaultValue"] = ["A limit with the unit 'flag' is an on/off feature: its default must be 0 (off) or 1 (on)."];
                ok = false;
            }

            if (ok)
            {
                valid.Add(new ValidLimit(key!, label!, description, unit, defaultValue));
            }
        }

        return errors.Count == 0;
    }

    public static bool IsFlag(string? unit) => string.Equals(unit, FlagUnit, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a limit value: a whole number of at least 0, or <c>null</c> for unlimited. It must be given outright, because leaving it out
    /// would otherwise silently mean "unlimited", the most generous value there is.
    /// </summary>
    public static bool TryReadValue(JsonElement element, out long? value, out string? error)
    {
        value = null;
        error = null;
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                return true;
            case JsonValueKind.Number when element.TryGetInt64(out var number) && number >= 0:
                value = number;
                return true;
            case JsonValueKind.Undefined:
                error = DefaultValueRequired;
                return false;
            default:
                error = "The value must be a whole number of at least 0, or null for unlimited.";
                return false;
        }
    }

    private const string DefaultValueRequired = "DefaultValue is required: a whole number of at least 0, or null for unlimited.";

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9._-]*[a-z0-9])?$")]
    private static partial Regex KeyPattern();
}

/// <summary>ENT-01: the numbers a product can limit. Plans (ENT-02) and per-tenant overrides (ENT-03) give them values.</summary>
public sealed class LimitDefinitionService(AuthDbContext db, AuditLog audit, TimeProvider timeProvider)
{
    public async Task<RegisterLimitsResult> RegisterAsync(
        Guid adminId, Guid applicationId, IReadOnlyList<ValidLimit> limits, CancellationToken cancellationToken)
    {
        // Registrations of one product run one at a time behind its row lock, so two at once cannot both insert the same new key.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var application = await db.LockApplicationAsync(applicationId, cancellationToken);
        if (application is null)
        {
            return new RegisterLimitsResult.ApplicationNotFound();
        }

        var existing = await db.LimitDefinitions.Where(limit => limit.ApplicationId == applicationId)
            .ToDictionaryAsync(limit => limit.Key, cancellationToken);

        var total = existing.Count + limits.Count(limit => !existing.ContainsKey(limit.Key));
        if (total > LimitDefinitionValidator.MaxLimitsPerProduct)
        {
            return new RegisterLimitsResult.TooMany(total);
        }

        var now = timeProvider.GetUtcNow();
        int created = 0, updated = 0, unchanged = 0;
        foreach (var input in limits)
        {
            if (!existing.TryGetValue(input.Key, out var limit))
            {
                limit = new LimitDefinition
                {
                    Id = Guid.CreateVersion7(),
                    ApplicationId = applicationId,
                    Key = input.Key,
                    Label = input.Label,
                    Description = input.Description,
                    Unit = input.Unit,
                    DefaultValue = input.DefaultValue,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.LimitDefinitions.Add(limit);
                existing[input.Key] = limit;
                created++;
            }
            else if (limit.Label != input.Label || limit.Description != input.Description || limit.Unit != input.Unit
                || limit.DefaultValue != input.DefaultValue)
            {
                // Updated in place under the same key, which is what plans and overrides refer to.
                limit.Label = input.Label;
                limit.Description = input.Description;
                limit.Unit = input.Unit;
                limit.DefaultValue = input.DefaultValue;
                limit.UpdatedAt = now;
                updated++;
            }
            else
            {
                unchanged++;
            }
        }

        // Repeating what is already there changes nothing, so it leaves no audit entry.
        if (created + updated > 0)
        {
            audit.Record(AuditActions.LimitsRegistered, AuditActor.User(adminId), detail: application.Key);
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        var all = existing.Values.OrderBy(limit => limit.Key, StringComparer.Ordinal).Select(ToDto).ToList();
        return new RegisterLimitsResult.Registered(new RegisterLimitsResponse(applicationId, created, updated, unchanged, all));
    }

    public static LimitDefinitionDto ToDto(LimitDefinition limit) =>
        new(limit.Key, limit.Label, limit.Description, limit.Unit, limit.DefaultValue, limit.CreatedAt, limit.UpdatedAt);
}
