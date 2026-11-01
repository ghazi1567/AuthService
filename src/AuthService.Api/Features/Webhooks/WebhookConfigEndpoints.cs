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
public sealed record SetWebhookRequest(string? Url, bool? RotateSecret);

public sealed record WebhookConfigResponse(string? Url, bool HasSecret);

/// <summary><see cref="SigningSecret"/> is in this response and nowhere else, ever, and only when it was just created or rotated.</summary>
public sealed record SetWebhookResponse(string Url, string? SigningSecret);

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

    private static async Task<IResult> GetAsync(ClaimsPrincipal caller, AuthDbContext db, CancellationToken cancellationToken)
    {
        var applicationId = caller.GetApplicationId() ?? throw new InvalidOperationException("The service-token policy guarantees an application id.");
        var application = await db.Applications.AsNoTracking()
            .Where(candidate => candidate.Id == applicationId)
            .Select(candidate => new WebhookConfigResponse(candidate.WebhookUrl, candidate.WebhookSecretProtected != null))
            .SingleAsync(cancellationToken);

        return Results.Ok(application);
    }

    private static async Task<IResult> SetAsync(
        SetWebhookRequest request,
        ClaimsPrincipal caller,
        AuthDbContext db,
        KeyEncryption keyEncryption,
        AuditLog audit,
        IOptions<WebhookOptions> options,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var uri = WebhookUrlValidator.TryValidate(request.Url, options.Value.AllowPrivateAddresses, out var error);
        if (uri is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(SetWebhookRequest.Url)] = [error!] });
        }

        var applicationId = caller.GetApplicationId() ?? throw new InvalidOperationException("The service-token policy guarantees an application id.");
        var application = await db.Applications.SingleAsync(candidate => candidate.Id == applicationId, cancellationToken);

        // A new key the first time, or when the product asks for one. Otherwise the product keeps verifying with the key it has.
        string? newSecret = null;
        if (application.WebhookSecretProtected is null || request.RotateSecret == true)
        {
            newSecret = WebhookSigner.NewSecret();
            application.WebhookSecretProtected = keyEncryption.Encrypt(Encoding.UTF8.GetBytes(newSecret));
        }

        application.WebhookUrl = uri.AbsoluteUri;
        audit.Record(AuditActions.WebhookConfigured, AuditActor.Product(applicationId), detail: application.Key);
        await db.SaveChangesAsync(cancellationToken);

        // The response may carry the signing secret, shown once: it must never be stored by a browser or proxy.
        httpContext.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new SetWebhookResponse(application.WebhookUrl, newSecret));
    }
}
