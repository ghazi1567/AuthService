using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AuthService.Authorization;

/// <summary>Installs a product from the Auth Service's address and its client secret alone.</summary>
public static class AuthServiceProductExtensions
{
    /// <summary>
    /// Sets the product up from two things: where the Auth Service is and the client secret the platform admin gave the product. The
    /// package learns the rest from the Auth Service (who issues tokens, the product's own audience, where the keys are) and then does
    /// what <c>AddAuthServiceAuthorization</c> does: a token is checked locally, with no call per request, against the published keys,
    /// the issuer, the product's own audience and its lifetime, only ES256 is accepted, and anything else is a clean 401 or 403. It also
    /// registers <see cref="IAuthServiceClient"/> for the calls a product makes as itself, such as subscribing an organisation.
    /// Use this or <c>AddAuthServiceAuthorization</c>, not both. Call <c>UseAuthentication</c> and <c>UseAuthorization</c> after it.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddAuthServiceProduct(options => builder.Configuration.GetSection("AuthService").Bind(options));
    /// // AuthService__Url=https://auth.example.com   AuthService__ClientSecret=cl_….the-secret
    /// </code>
    /// </example>
    public static IServiceCollection AddAuthServiceProduct(this IServiceCollection services, Action<AuthServiceProductOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<AuthServiceProductOptions>()
            .Configure(configure)
            .Validate(options => options.Url is { IsAbsoluteUri: true } url && url.Scheme is "http" or "https",
                "AuthServiceProductOptions.Url is required: the address of the Auth Service, such as https://auth.example.com.")
            .Validate(
                options => options.Url is null || options.Url.Scheme == Uri.UriSchemeHttps || options.AllowInsecureUrl,
                "The Auth Service address must be https. Set AllowInsecureUrl only for local development.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.ClientSecret),
                "AuthServiceProductOptions.ClientSecret is required: the client secret the platform admin gave the product.")
            .ValidateOnStart();

        services.AddHttpClient(AuthServiceProductOptions.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        });

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ProductConnection>();
        services.AddSingleton<ProductKeysConfigurationManager>();
        services.AddSingleton<IAuthServiceClient, AuthServiceClient>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AuthServiceProductOptions>, ProductKeysConfigurationManager, ProductConnection, TimeProvider>(
                (bearer, settings, keys, connection, time) =>
                    BearerSetup.Apply(bearer, keys, time, settings.Value.ClockSkewSeconds, parameters =>
                    {
                        // The issuer and the audience are what the Auth Service said, read before the first token is judged (the keys
                        // are asked for first, and asking is what reads them). With nothing known, neither matches and the token is refused.
                        parameters.IssuerValidator = (issuer, _, _) =>
                            connection.Current is { } known && string.Equals(issuer, known.Issuer, StringComparison.Ordinal)
                                ? issuer
                                : throw new SecurityTokenInvalidIssuerException("The issuer is not the Auth Service's.");
                        parameters.AudienceValidator = (audiences, _, _) =>
                            connection.Current is { } known && audiences.Any(audience => string.Equals(audience, known.Audience, StringComparison.Ordinal));
                    }));

        BearerSetup.AddEnforcement(services);

        return services;
    }
}
