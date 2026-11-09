using System.Security.Claims;
using AuthService.Api.Common;
using AuthService.Api.Tokens;
using AuthService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.Entitlements;

/// <summary>
/// ENT-02: a product's plans. A platform admin creates, replaces, activates or deactivates and (while never assigned) deletes them; the
/// product reads its own with its service token. Lists are paged by key (<c>{items,next}</c>).
/// </summary>
public static class PlanEndpoints
{
    /// <summary>The product's own plans, read with its service token.</summary>
    public const string CurrentApplicationRoute = "/api/auth/applications/me/plans";

    private const string PlansRoute = "/applications/{applicationId:guid}/plans";
    private const string PlanRoute = PlansRoute + "/{planKey}";

    public static RouteGroupBuilder MapPlansAdmin(this RouteGroupBuilder admin)
    {
        admin.MapGet(PlansRoute, ListAsync);
        admin.MapPost(PlansRoute, CreateAsync);
        admin.MapGet(PlanRoute, GetAsync);
        admin.MapPut(PlanRoute, ReplaceAsync);
        admin.MapPut(PlanRoute + "/active", SetActiveAsync);
        admin.MapDelete(PlanRoute, DeleteAsync);
        return admin;
    }

    public static IEndpointRouteBuilder MapCurrentApplicationPlans(this IEndpointRouteBuilder app)
    {
        app.MapGet(CurrentApplicationRoute, ListOwnAsync).RequireAuthorization(IdentityAuthentication.ServicePolicyName);
        return app;
    }

    private static async Task<IResult> CreateAsync(
        Guid applicationId, CreatePlanRequest request, ClaimsPrincipal caller, PlanService plans, CancellationToken cancellationToken)
    {
        var keyOk = PlanValidator.TryValidateKey(request.Key, out var key, out var keyError);
        var fieldsOk = PlanValidator.TryValidateFields(request.Name, request.Description, request.Values, out var fields, out var errors);
        if (!keyOk)
        {
            errors["Key"] = [keyError!];
        }

        if (!keyOk || !fieldsOk)
        {
            return Results.ValidationProblem(errors);
        }

        return ToResult(await plans.CreateAsync(AdminId(caller), applicationId, key, request.IsActive ?? true, fields!, cancellationToken));
    }

    /// <summary>Replaces a plan's name, description and the whole set of its values; a limit no longer listed falls back to its default.</summary>
    private static async Task<IResult> ReplaceAsync(
        Guid applicationId, string planKey, ReplacePlanRequest request, ClaimsPrincipal caller, PlanService plans, CancellationToken cancellationToken)
    {
        if (!PlanValidator.TryValidateFields(request.Name, request.Description, request.Values, out var fields, out var errors))
        {
            return Results.ValidationProblem(errors);
        }

        return ToResult(await plans.ReplaceAsync(AdminId(caller), applicationId, Normalise(planKey), fields!, cancellationToken));
    }

    /// <summary>A deactivated plan cannot be newly assigned; tenants that already have it keep it.</summary>
    private static async Task<IResult> SetActiveAsync(
        Guid applicationId, string planKey, SetPlanActiveRequest request, ClaimsPrincipal caller, PlanService plans, CancellationToken cancellationToken)
    {
        if (request.IsActive is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["IsActive"] = ["IsActive is required."] });
        }

        return ToResult(await plans.SetActiveAsync(AdminId(caller), applicationId, Normalise(planKey), request.IsActive.Value, cancellationToken));
    }

    private static async Task<IResult> DeleteAsync(Guid applicationId, string planKey, ClaimsPrincipal caller, PlanService plans, CancellationToken cancellationToken) =>
        ToResult(await plans.DeleteAsync(AdminId(caller), applicationId, Normalise(planKey), cancellationToken));

    private static async Task<IResult> GetAsync(Guid applicationId, string planKey, AuthDbContext db, PlanService plans, CancellationToken cancellationToken)
    {
        if (!await ApplicationExistsAsync(db, applicationId, cancellationToken))
        {
            return ApplicationNotFound();
        }

        var key = Normalise(planKey);
        var plan = await db.Plans.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.ApplicationId == applicationId && candidate.Key == key, cancellationToken);
        if (plan is null)
        {
            return PlanNotFound();
        }

        var values = await plans.ValuesAsync([plan.Id], cancellationToken);
        return Results.Ok(PlanService.ToDto(plan, values.GetValueOrDefault(plan.Id) ?? new Dictionary<string, long?>()));
    }

    private static async Task<IResult> ListAsync(
        Guid applicationId, AuthDbContext db, PlanService plans, [AsParameters] PageQuery page, CancellationToken cancellationToken)
    {
        if (page.Problem(byName: true) is { } problem)
        {
            return problem;
        }

        if (!await ApplicationExistsAsync(db, applicationId, cancellationToken))
        {
            return ApplicationNotFound();
        }

        return Results.Ok(await PageAsync(db, plans, applicationId, page, cancellationToken));
    }

    /// <summary>A product reads its own plans, active or not. Which product it is comes only from the verified token.</summary>
    private static async Task<IResult> ListOwnAsync(
        ClaimsPrincipal caller, AuthDbContext db, PlanService plans, [AsParameters] PageQuery page, CancellationToken cancellationToken)
    {
        if (page.Problem(byName: true) is { } problem)
        {
            return problem;
        }

        var applicationId = caller.GetApplicationId() ?? throw new InvalidOperationException("The service-token policy guarantees an application id.");
        return Results.Ok(await PageAsync(db, plans, applicationId, page, cancellationToken));
    }

    private static async Task<PagedResponse<PlanDto, NameCursor>> PageAsync(
        AuthDbContext db, PlanService plans, Guid applicationId, PageQuery page, CancellationToken cancellationToken)
    {
        var rows = await page.ByNameAsync(
            db.Plans.AsNoTracking().Where(plan => plan.ApplicationId == applicationId),
            plan => plan.Key,
            plan => plan.Id,
            cancellationToken);

        var values = await plans.ValuesAsync(rows.Rows.Select(plan => plan.Id).ToList(), cancellationToken);
        return rows.Map(items => items.Select(plan => PlanService.ToDto(plan, values.GetValueOrDefault(plan.Id) ?? new Dictionary<string, long?>())).ToList());
    }

    private static IResult ToResult(PlanResult result) => result switch
    {
        PlanResult.Saved { Created: true } saved => Results.Json(saved.Plan, statusCode: StatusCodes.Status201Created),
        PlanResult.Saved saved => Results.Ok(saved.Plan),
        PlanResult.Deleted => Results.NoContent(),
        PlanResult.PlanNotFound => PlanNotFound(),
        PlanResult.KeyTaken => ApiProblem.Create(StatusCodes.Status409Conflict, "plan_key_taken",
            "Plan key already used", "This product already has a plan with this key."),
        PlanResult.TooMany => ApiProblem.Create(StatusCodes.Status409Conflict, "too_many_plans",
            "Too many plans", $"A product can have at most {PlanValidator.MaxPlansPerProduct} plans. Deactivate or delete one first."),
        PlanResult.LimitUnknown unknown => ApiProblem.Create(StatusCodes.Status400BadRequest, "limit_unknown",
            "Unknown limit", $"These keys are not limits of this product: {string.Join(", ", unknown.Keys)}. Add them as limits first."),
        PlanResult.ValueInvalid invalid => ApiProblem.Create(StatusCodes.Status400BadRequest, "value_invalid", "Value not allowed", invalid.Message),
        PlanResult.InUse => ApiProblem.Create(StatusCodes.Status409Conflict, "plan_in_use",
            "Plan has been assigned", "This plan has been assigned to a subscription, so it cannot be deleted. Deactivate it instead."),
        _ => ApplicationNotFound(),
    };

    private static Task<bool> ApplicationExistsAsync(AuthDbContext db, Guid applicationId, CancellationToken cancellationToken) =>
        db.Applications.AsNoTracking().AnyAsync(application => application.Id == applicationId && !application.IsBuiltIn, cancellationToken);

    private static string Normalise(string planKey) => planKey.Trim().ToLowerInvariant();

    private static Guid AdminId(ClaimsPrincipal caller) =>
        caller.GetUserId() ?? throw new InvalidOperationException("The platform-admin policy guarantees a user id.");

    private static IResult PlanNotFound() =>
        ApiProblem.Create(StatusCodes.Status404NotFound, "plan_not_found", "Plan not found", "This product has no plan with this key.");

    private static IResult ApplicationNotFound() =>
        ApiProblem.Create(StatusCodes.Status404NotFound, "application_not_found", "Application not found", "No application has this id.");
}
