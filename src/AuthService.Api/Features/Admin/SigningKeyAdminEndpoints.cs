using System.Security.Claims;
using AuthService.Api.Common;
using AuthService.Api.Features.Audit;
using AuthService.Api.Tokens;

namespace AuthService.Api.Features.Admin;

/// <summary>The key that signs tokens as of now.</summary>
public sealed record RotateSigningKeyResponse(string KeyId);

public static class SigningKeyAdminEndpoints
{
    public const string RotateRoute = "/api/auth/admin/signing-keys/rotate";

    public static RouteGroupBuilder MapSigningKeyAdmin(this RouteGroupBuilder admin)
    {
        admin.MapPost("/signing-keys/rotate", RotateAsync);
        return admin;
    }

    /// <summary>
    /// Emergency rotation (TOK-02): a new key signs from this moment and every earlier key leaves the JWKS at once, so tokens
    /// it signed stop validating. For when a key may be compromised. The event is audit-logged under the admin who made it.
    /// </summary>
    private static async Task<IResult> RotateAsync(ClaimsPrincipal caller, SigningKeyService signingKeys, CancellationToken cancellationToken)
    {
        var adminId = caller.GetUserId() ?? throw new InvalidOperationException("The platform-admin policy guarantees a user id.");

        await signingKeys.RotateNowAsync(AuditActor.User(adminId), cancellationToken);

        return Results.Ok(new RotateSigningKeyResponse(signingKeys.CurrentSigningKey.KeyId!));
    }
}
