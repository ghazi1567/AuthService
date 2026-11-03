using System.Security.Claims;
using System.Text;
using AuthService.Api.Common;
using AuthService.Api.Features.Audit;
using AuthService.Api.Tokens;
using AuthService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthService.Api.Features.Webhooks;

/// <summary>Body of <c>PUT /api/auth/applications/me/webhook</c>.</summary>
public sealed record SetWebhookRequest(string? Url, bool? RotateSecret, int? OverlapHours = null);

/// <summary><see cref="PreviousSecretExpiresAt"/> is set only while a rotated-out secret still signs (SUB-07).</summary>
public sealed record WebhookConfigResponse(string? Url, bool HasSecret, DateTimeOffset? PreviousSecretExpiresAt = null);

/// <summary><see cref="SigningSecret"/> is in this response and nowhere else, ever, and only when it was just created or rotated.</summary>
public sealed record SetWebhookResponse(string Url, string? SigningSecret, DateTimeOffset? PreviousSecretExpiresAt = null);

/// <summary>A product registers where it wants to be told about subscriptions, and gets the key to verify them with (SUB-06).</summary>
public static class WebhookConfigEndpoints
{
    public const string Route = "/api/auth/applications/me/webhook";

    public static IEndpointRouteBuilder MapWebhookConfig(this IEndpointRouteBuilder app)
    {
        // Service token only: which product it is comes from the verified token, so a product can only set its own callback.
        app.MapPut(Route, SetAsync).RequireAuthorization(IdentityAuthentication.ServicePolicyName);
        app.MapGet(Route, GetAsync).RequireAuthorization(IdentityAuthentication.ServicePolicyName);
        return app;
    }

    private static async Task<IResult> GetAsync(ClaimsPrincipal caller, AuthDbContext db, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var applicationId = caller.GetApplicationId() ?? throw new InvalidOperationException("The service-token policy guarantees an application id.");
        var application = await db.Applications.AsNoTracking()
            .Where(candidate => candidate.Id == applicationId)
            .Select(candidate => new
            {
                candidate.WebhookUrl,
                HasSecret = candidate.WebhookSecretProtected != null,
                candidate.WebhookPreviousSecretProtected,
                candidate.WebhookPreviousSecretExpiresAt,
            })
            .SingleAsync(cancellationToken);

        var now = timeProvider.GetUtcNow();
        var overlapEnds = application.WebhookPreviousSecretProtected is not null && application.WebhookPreviousSecretExpiresAt > now
            ? application.WebhookPreviousSecretExpiresAt
            : null;
        return Results.Ok(new WebhookConfigResponse(application.WebhookUrl, application.HasSecret, overlapEnds));
    }

    private static async Task<IResult> SetAsync(
        SetWebhookRequest request,
        ClaimsPrincipal caller,
        AuthDbContext db,
        KeyEncryption keyEncryption,
        AuditLog audit,
        IOptions<WebhookOptions> options,
        TimeProvider timeProvider,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var uri = WebhookUrlValidator.TryValidate(request.Url, options.Value.AllowPrivateAddresses, out var error);
        if (uri is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(SetWebhookRequest.Url)] = [error!] });
        }

        if (request.OverlapHours is < 0 or > WebhookOptions.MaxSecretOverlapHours)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(SetWebhookRequest.OverlapHours)] = [$"OverlapHours must be between 0 and {WebhookOptions.MaxSecretOverlapHours}."],
            });
        }

        var applicationId = caller.GetApplicationId() ?? throw new InvalidOperationException("The service-token policy guarantees an application id.");
        var application = await db.Applications.SingleAsync(candidate => candidate.Id == applicationId, cancellationToken);

        // A new key the first time, or when the product asks for one. Otherwise the product keeps verifying with the key it has.
        var now = timeProvider.GetUtcNow();
        string? newSecret = null;
        if (application.WebhookSecretProtected is null || request.RotateSecret == true)
        {
            var rotating = application.WebhookSecretProtected is not null;
            var overlapHours = request.OverlapHours ?? options.Value.SecretOverlapHours;

            // The secret being replaced keeps signing for the overlap. If one was already overlapping it is dropped here, so there are
            // never more than two. With no overlap the old secret stops signing at once: how a leaked one is cut off.
            if (rotating && overlapHours > 0)
            {
                application.WebhookPreviousSecretProtected = application.WebhookSecretProtected;
                application.WebhookPreviousSecretExpiresAt = now.AddHours(overlapHours);
            }
            else
            {
                application.WebhookPreviousSecretProtected = null;
                application.WebhookPreviousSecretExpiresAt = null;
            }

            newSecret = WebhookSigner.NewSecret();
            application.WebhookSecretProtected = keyEncryption.Encrypt(Encoding.UTF8.GetBytes(newSecret));

            if (rotating)
            {
                audit.Record(AuditActions.WebhookSecretRotated, AuditActor.Product(applicationId), detail: $"overlap_hours_{overlapHours}");
            }
        }

        application.WebhookUrl = uri.AbsoluteUri;
        audit.Record(AuditActions.WebhookConfigured, AuditActor.Product(applicationId), detail: application.Key);
        await db.SaveChangesAsync(cancellationToken);

        // The response may carry the signing secret, shown once: it must never be stored by a browser or proxy.
        httpContext.Response.Headers.CacheControl = "no-store";
        var overlapEnds = application.WebhookPreviousSecretProtected is not null ? application.WebhookPreviousSecretExpiresAt : null;
        return Results.Ok(new SetWebhookResponse(application.WebhookUrl, newSecret, overlapEnds));
    }
}
