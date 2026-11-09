using AuthService.Api.RateLimiting;
using System.Text.Json;
using System.Text.Json.Serialization;
using AuthService.Api.Common;
using AuthService.Api.Tokens;
using Microsoft.Extensions.Primitives;

namespace AuthService.Api.Features.Applications;

/// <summary>Body of <c>POST /api/auth/token</c>, named as in OAuth 2.0 so standard client libraries can send it.</summary>
public sealed record ServiceTokenRequest(
    [property: JsonPropertyName("grant_type")] string? GrantType,
    [property: JsonPropertyName("client_id")] string? ClientId,
    [property: JsonPropertyName("client_secret")] string? ClientSecret);

public sealed record ServiceTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn);

/// <summary>The OAuth 2.0 client-credentials grant, for a product calling the Auth Service (APP-05).</summary>
public static class ServiceTokenEndpoints
{
    public const string Route = "/api/auth/token";
    public const string RateLimitPolicy = "service-token";
    public const string ClientCredentialsGrant = "client_credentials";

    public static IEndpointRouteBuilder MapServiceToken(this IEndpointRouteBuilder app)
    {
        app.MapPost(Route, TokenAsync).AllowAnonymous().RequireSharedRateLimit(RateLimitPolicy);
        return app;
    }

    private static async Task<IResult> TokenAsync(
        HttpContext httpContext,
        ServiceTokenService tokens,
        CancellationToken cancellationToken)
    {
        // A token response must never be stored by a browser or proxy, whatever the outcome.
        httpContext.Response.Headers.CacheControl = "no-store";
        httpContext.Response.Headers.Pragma = "no-cache";

        var request = await ReadRequestAsync(httpContext.Request, cancellationToken);

        // The client secret may be the one combined string a product team is given (client id, a dot, the secret), with or without the
        // client id beside it (ONB-03). A client id that names another client is left alone and simply fails like any wrong secret.
        var clientId = request?.ClientId;
        var clientSecret = request?.ClientSecret;
        if (ClientCredential.TryParse(clientSecret, out var combinedId, out var combinedSecret) && (string.IsNullOrEmpty(clientId) || clientId == combinedId))
        {
            clientId = combinedId;
            clientSecret = combinedSecret;
        }

        if (request is null || string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            return ApiProblem.Create(StatusCodes.Status400BadRequest, "invalid_request", "Invalid request",
                "grant_type and client_secret are required, as a JSON or form body: the client secret you were given, or client_id and client_secret.");
        }

        if (request.GrantType != ClientCredentialsGrant)
        {
            return ApiProblem.Create(StatusCodes.Status400BadRequest, "unsupported_grant_type", "Unsupported grant type",
                $"Only the {ClientCredentialsGrant} grant is supported.");
        }

        var token = await tokens.IssueAsync(clientId, clientSecret, cancellationToken);

        // The same answer for an unknown client, a wrong secret and an expired one, so the response reveals nothing about which.
        return token is null
            ? ApiProblem.Create(StatusCodes.Status401Unauthorized, "invalid_client", "Invalid client", "The client credentials are not valid.")
            : Results.Ok(token);
    }

    /// <summary>Reads the OAuth form encoding or JSON. Returns null when the body is neither or cannot be read.</summary>
    private static async Task<ServiceTokenRequest?> ReadRequestAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (request.HasFormContentType)
            {
                var form = await request.ReadFormAsync(cancellationToken);
                return new ServiceTokenRequest(Single(form["grant_type"]), Single(form["client_id"]), Single(form["client_secret"]));
            }

            if (request.HasJsonContentType())
            {
                return await request.ReadFromJsonAsync<ServiceTokenRequest>(cancellationToken);
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or BadHttpRequestException)
        {
            // A malformed body is the caller's mistake, reported as 400 like any other unusable request.
        }

        return null;
    }

    private static string? Single(StringValues values) => values.Count == 1 ? values[0] : null;
}
