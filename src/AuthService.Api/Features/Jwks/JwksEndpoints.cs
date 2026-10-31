using AuthService.Api.Tokens;

namespace AuthService.Api.Features.Jwks;

public static class JwksEndpoints
{
    public const string Route = "/.well-known/jwks.json";

    public static IEndpointRouteBuilder MapJwks(this IEndpointRouteBuilder app)
    {
        // No authentication, by design: it holds only public keys, and products need it to check tokens without a login.
        app.MapGet(Route, GetJwks).AllowAnonymous();
        return app;
    }

    private static IResult GetJwks(SigningKeyService signingKeys, HttpContext httpContext)
    {
        // Products may cache it briefly. Keys are published a day before they sign, so a short cache never misses one.
        httpContext.Response.Headers.CacheControl = "public, max-age=300";

        return Results.Json(new
        {
            keys = signingKeys.GetPublishedKeys().Select(key => new
            {
                kty = "EC",
                crv = "P-256",
                use = "sig",
                alg = "ES256",
                kid = key.KeyId,
                x = key.X,
                y = key.Y,
            }),
        });
    }
}
