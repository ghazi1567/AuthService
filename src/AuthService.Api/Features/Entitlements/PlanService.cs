using System.Text.Json;
using AuthService.Api.Features.Applications;
using AuthService.Api.Features.Audit;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.Entitlements;

/// <summary>A plan's name, description and values after the checks that need no database.</summary>
public sealed record ValidPlanFields(string Name, string? Description, IReadOnlyDictionary<string, long?> Values);

public abstract record PlanResult
{
    public sealed record Saved(PlanDto Plan, bool Created) : PlanResult;

    public sealed record Deleted : PlanResult;

    public sealed record ApplicationNotFound : PlanResult;

    public sealed record PlanNotFound : PlanResult;

    public sealed record KeyTaken : PlanResult;

    public sealed record TooMany : PlanResult;

    /// <summary>Keys in the values that are not limits of this product.</summary>
    public sealed record LimitUnknown(IReadOnlyList<string> Keys) : PlanResult;

    /// <summary>A value that the limit does not allow (an on/off limit given something other than 0 or 1).</summary>
    public sealed record ValueInvalid(string Key, string Message) : PlanResult;

    /// <summary>The plan was assigned to a subscription at some point, so it can only be deactivated.</summary>
    public sealed record InUse : PlanResult;
}

public static class PlanValidator
{
    public const int MaxPlansPerProduct = 20;
    public const int MaxNameLength = 200;
    public const int MaxDescriptionLength = 1000;

    public static bool TryValidateKey(string? input, out string key, out string? error)
    {
        // A plan key follows the limit-key rules: it names the plan in events and in the product's code.
        key = input?.Trim().ToLowerInvariant() ?? "";
        if (!LimitDefinitionValidator.IsValidKey(key))
        {
            error = $"Key must be 1 to {LimitDefinitionValidator.MaxKeyLength} lowercase letters, digits and . _ - characters, such as growth.";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryValidateFields(
        string? nameInput, string? descriptionInput, Dictionary<string, JsonElement>? valuesInput,
        out ValidPlanFields? valid, out Dictionary<string, string[]> errors)
    {
        errors = [];
        valid = null;

        var name = nameInput?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength)
        {
            errors["Name"] = [$"Name is required and must be at most {MaxNameLength} characters."];
        }

        var description = string.IsNullOrWhiteSpace(descriptionInput) ? null : descriptionInput.Trim();
        if (description is { Length: > MaxDescriptionLength })
        {
            errors["Description"] = [$"Description must be at most {MaxDescriptionLength} characters."];
        }

        var values = new Dictionary<string, long?>(StringComparer.Ordinal);
        if (valuesInput is null)
        {
            // Sent outright, even as {}, so that forgetting it never wipes a plan's values.
            errors["Values"] = ["Values is required: an object of limit keys to a whole number, or null for unlimited. Send {} for none."];
        }
        else
        {
            foreach (var (rawKey, element) in valuesInput)
            {
                var key = rawKey.Trim().ToLowerInvariant();
                if (!LimitDefinitionValidator.TryReadValue(element, out var value, out var error))
                {
                    errors[$"Values.{rawKey}"] = [error!];
                }
                else if (!values.TryAdd(key, value))
                {
                    errors[$"Values.{rawKey}"] = [$"Key '{key}' appears more than once."];
                }
            }
        }

        if (errors.Count > 0)
        {
            return false;
        }

        valid = new ValidPlanFields(name!, description, values);
        return true;
    }
}

/// <summary>
/// ENT-02: plans, a product's named templates of limit values. Assigning a plan to a tenant and what a plan edit does to tenants already on
/// it come with ENT-03; until then a plan only exists in the catalogue.
/// </summary>
public sealed class PlanService(AuthDbContext db, AuditLog audit, TimeProvider timeProvider)
{
    public async Task<PlanResult> CreateAsync(
        Guid adminId, Guid applicationId, string key, bool isActive, ValidPlanFields fields, CancellationToken cancellationToken)
    {
        // Changes to one product's plans run one at a time behind its row lock, so the 20-plan cap and key uniqueness hold.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var application = await db.LockApplicationAsync(applicationId, cancellationToken);
        if (application is null)
        {
            return new PlanResult.ApplicationNotFound();
        }

        var plans = await db.Plans.Where(plan => plan.ApplicationId == applicationId).Select(plan => plan.Key).ToListAsync(cancellationToken);
        if (plans.Contains(key))
        {
            return new PlanResult.KeyTaken();
        }

        if (plans.Count >= PlanValidator.MaxPlansPerProduct)
        {
            return new PlanResult.TooMany();
        }

        var limits = await LimitsAsync(applicationId, cancellationToken);
        if (CheckValues(fields.Values, limits) is { } refused)
        {
            return refused;
        }

        var now = timeProvider.GetUtcNow();
        var created = new Plan
        {
            Id = Guid.CreateVersion7(),
            ApplicationId = applicationId,
            Key = key,
            Name = fields.Name,
            Description = fields.Description,
            IsActive = isActive,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Plans.Add(created);
        AddValues(created.Id, fields.Values, limits);

        audit.Record(AuditActions.PlanCreated, AuditActor.User(adminId), detail: Detail(application, key));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new PlanResult.Saved(ToDto(created, fields.Values), Created: true);
    }

    public async Task<PlanResult> ReplaceAsync(
        Guid adminId, Guid applicationId, string key, ValidPlanFields fields, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var application = await db.LockApplicationAsync(applicationId, cancellationToken);
        if (application is null)
        {
            return new PlanResult.ApplicationNotFound();
        }

        var plan = await db.Plans.SingleOrDefaultAsync(candidate => candidate.ApplicationId == applicationId && candidate.Key == key, cancellationToken);
        if (plan is null)
        {
            return new PlanResult.PlanNotFound();
        }

        var limits = await LimitsAsync(applicationId, cancellationToken);
        if (CheckValues(fields.Values, limits) is { } refused)
        {
            return refused;
        }

        var current = await ValuesAsync([plan.Id], cancellationToken);
        var before = current.GetValueOrDefault(plan.Id) ?? new Dictionary<string, long?>();
        var valuesChanged = before.Count != fields.Values.Count
            || fields.Values.Any(pair => !before.TryGetValue(pair.Key, out var old) || old != pair.Value);

        if (valuesChanged || plan.Name != fields.Name || plan.Description != fields.Description)
        {
            plan.Name = fields.Name;
            plan.Description = fields.Description;
            plan.UpdatedAt = timeProvider.GetUtcNow();
            if (valuesChanged)
            {
                // The whole set is replaced: a limit no longer listed falls back to its default.
                await db.PlanValues.Where(value => value.PlanId == plan.Id).ExecuteDeleteAsync(cancellationToken);
                AddValues(plan.Id, fields.Values, limits);
            }

            audit.Record(AuditActions.PlanUpdated, AuditActor.User(adminId), detail: Detail(application, key));
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new PlanResult.Saved(ToDto(plan, fields.Values), Created: false);
    }

    public async Task<PlanResult> SetActiveAsync(Guid adminId, Guid applicationId, string key, bool isActive, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var application = await db.LockApplicationAsync(applicationId, cancellationToken);
        if (application is null)
        {
            return new PlanResult.ApplicationNotFound();
        }

        var plan = await db.Plans.SingleOrDefaultAsync(candidate => candidate.ApplicationId == applicationId && candidate.Key == key, cancellationToken);
        if (plan is null)
        {
            return new PlanResult.PlanNotFound();
        }

        // Saying the state it already has is not a change, so it leaves no audit entry.
        if (plan.IsActive != isActive)
        {
            plan.IsActive = isActive;
            plan.UpdatedAt = timeProvider.GetUtcNow();
            audit.Record(isActive ? AuditActions.PlanActivated : AuditActions.PlanDeactivated, AuditActor.User(adminId), detail: Detail(application, key));
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        var values = await ValuesAsync([plan.Id], cancellationToken);
        return new PlanResult.Saved(ToDto(plan, values.GetValueOrDefault(plan.Id) ?? new Dictionary<string, long?>()), Created: false);
    }

    public async Task<PlanResult> DeleteAsync(Guid adminId, Guid applicationId, string key, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var application = await db.LockApplicationAsync(applicationId, cancellationToken);
        if (application is null)
        {
            return new PlanResult.ApplicationNotFound();
        }

        var plan = await db.Plans.SingleOrDefaultAsync(candidate => candidate.ApplicationId == applicationId && candidate.Key == key, cancellationToken);
        if (plan is null)
        {
            return new PlanResult.PlanNotFound();
        }

        if (plan.EverAssigned)
        {
            return new PlanResult.InUse();
        }

        db.Plans.Remove(plan);
        audit.Record(AuditActions.PlanDeleted, AuditActor.User(adminId), detail: Detail(application, key));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new PlanResult.Deleted();
    }

    /// <summary>Each plan's values by limit key, for the plans asked about.</summary>
    public async Task<Dictionary<Guid, Dictionary<string, long?>>> ValuesAsync(IReadOnlyCollection<Guid> planIds, CancellationToken cancellationToken)
    {
        var rows = await (
            from value in db.PlanValues.AsNoTracking()
            join limit in db.LimitDefinitions.AsNoTracking() on value.LimitDefinitionId equals limit.Id
            where planIds.Contains(value.PlanId)
            select new { value.PlanId, limit.Key, value.Value })
            .ToListAsync(cancellationToken);

        return rows.GroupBy(row => row.PlanId)
            .ToDictionary(group => group.Key, group => group.ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal));
    }

    public static PlanDto ToDto(Plan plan, IReadOnlyDictionary<string, long?> values) =>
        new(plan.Key, plan.Name, plan.Description, plan.IsActive, !plan.EverAssigned,
            new SortedDictionary<string, long?>(values.ToDictionary(), StringComparer.Ordinal), plan.CreatedAt, plan.UpdatedAt);

    private Task<Dictionary<string, LimitDefinition>> LimitsAsync(Guid applicationId, CancellationToken cancellationToken) =>
        db.LimitDefinitions.Where(limit => limit.ApplicationId == applicationId).ToDictionaryAsync(limit => limit.Key, cancellationToken);

    private static PlanResult? CheckValues(IReadOnlyDictionary<string, long?> values, Dictionary<string, LimitDefinition> limits)
    {
        var unknown = values.Keys.Where(key => !limits.ContainsKey(key)).Order(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            return new PlanResult.LimitUnknown(unknown);
        }

        foreach (var (key, value) in values)
        {
            if (LimitDefinitionValidator.IsFlag(limits[key].Unit) && value is not (0 or 1))
            {
                return new PlanResult.ValueInvalid(key, $"'{key}' is an on/off feature: its value must be 0 (off) or 1 (on).");
            }

            if (key == LimitDefinitionValidator.SeatsKey && value == 0)
            {
                return new PlanResult.ValueInvalid(key, LimitDefinitionValidator.SeatsRule);
            }
        }

        return null;
    }

    private void AddValues(Guid planId, IReadOnlyDictionary<string, long?> values, Dictionary<string, LimitDefinition> limits)
    {
        foreach (var (key, value) in values)
        {
            db.PlanValues.Add(new PlanValue { Id = Guid.CreateVersion7(), PlanId = planId, LimitDefinitionId = limits[key].Id, Value = value });
        }
    }

    private static string Detail(Application application, string planKey) => $"{application.Key}.{planKey}";
}
