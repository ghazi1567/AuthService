using AuthService.Api.Common;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AuthService.Api.Tokens;

/// <summary>
/// Bearer authentication for the identity tokens issued at login and refresh. An identity token names the user only: it has
/// no tenant, no permissions and no audience. Endpoints that take it use <see cref="PolicyName"/>.
/// </summary>
public static class IdentityAuthentication
{
    public const string PolicyName = "IdentityToken";

    /// <summary>An identity token whose user is a platform admin. Applied to every endpoint under <c>/api/auth/admin</c>.</summary>
    public const string PlatformAdminPolicyName = "PlatformAdmin";

    public static IServiceCollection AddIdentityTokenAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // Configured when first needed, so it uses the signing key and settings the running service really has.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<SigningKeyService, IOptions<IdentityTokenOptions>>((bearer, signingKeys, tokenOptions) =>
            {
                // Keep claim names as issued ("sub"), not renamed to long Microsoft claim types.
                bearer.MapInboundClaims = false;

                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,

                    // The key is chosen by the token's kid among the keys currently published, so a token signed by the
                    // previous key still validates during the rotation overlap and not after it.
                    IssuerSigningKeyResolver = (_, _, kid, _) => signingKeys.GetValidationKeys(kid),

                    // Only the algorithm we sign with, so a token cannot pick a weaker one.
                    ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],

                    ValidateIssuer = true,
                    ValidIssuer = tokenOptions.Value.Issuer,

                    // Identity tokens have no audience. Product tokens do, and are refused by the policy below.
                    ValidateAudience = false,

                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = JwtRegisteredClaimNames.Sub,
                };

                bearer.Events = new JwtBearerEvents
                {
                    // The same problem response as every other error, instead of an empty 401.
                    OnChallenge = async context =>
                    {
                        context.HandleResponse();
                        context.Response.Headers.WWWAuthenticate = JwtBearerDefaults.AuthenticationScheme;
                        await ApiProblem.Create(
                                StatusCodes.Status401Unauthorized,
                                "unauthorized",
                                "Unauthorized",
                                "A valid access token is required.")
                            .ExecuteAsync(context.HttpContext);
                    },
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(PolicyName, policy => IdentityToken(policy))
            .AddPolicy(PlatformAdminPolicyName, policy => IdentityToken(policy).AddRequirements(new PlatformAdminRequirement()));

        services.AddScoped<IAuthorizationHandler, PlatformAdminHandler>();

        return services;
    }

    private static AuthorizationPolicyBuilder IdentityToken(AuthorizationPolicyBuilder policy) => policy
        .RequireAuthenticatedUser()
        .RequireClaim("sub")

        // A token with an audience belongs to one product (TOK-01) and must not work on identity endpoints.
        .RequireAssertion(context => !context.User.HasClaim(claim => claim.Type == "aud"));
}
