using System.Globalization;
using AuthService.Api.Common;
using AuthService.Api.Features.Applications;
using AuthService.Api.Features.EmailVerification;
using AuthService.Api.Features.Login;
using AuthService.Api.Features.PasswordReset;
using AuthService.Api.Features.TokenExchange;
using AuthService.Api.Tokens;
using Microsoft.Extensions.Options;

namespace AuthService.Api.RateLimiting;

/// <summary>Marks an endpoint as limited by the named policy. Read by <see cref="SharedRateLimitMiddleware"/>.</summary>
public sealed record SharedRateLimitMetadata(string Policy);

public static class SharedRateLimitExtensions
{
    /// <summary>Limits an endpoint by a named policy (see <see cref="RateLimitPolicies"/>), counted in the configured store.</summary>
    public static TBuilder RequireSharedRateLimit<TBuilder>(this TBuilder builder, string policy)
        where TBuilder : IEndpointConventionBuilder => builder.WithMetadata(new SharedRateLimitMetadata(policy));
}

/// <summary>What a policy says about one request: whose counter it is, how many uses are allowed and over how long.</summary>
public readonly record struct RateLimitRule(string Key, int Limit, TimeSpan Window);

/// <summary>
/// The limits, one per policy (TEN-12, TEN-10, APP-05, TOK-05, TEN-11). Settings are read per request, so configuration added after
/// startup (test overrides) still applies. Behind a proxy, list the proxy in <c>Auth:Proxy</c> so the address is the client's.
///
/// <para>
/// When the store cannot be reached no limit fails open. Each of these endpoints needs the same database the counters live in, so a
/// store that is down means the endpoint could not have worked anyway, and letting requests through unlimited would only help an
/// attacker who can make the store fail. They are refused with 503 instead (decision 0008).
/// </para>
/// </summary>
public static class RateLimitPolicies
{
    public static RateLimitRule Resolve(string policy, HttpContext httpContext)
    {
        var services = httpContext.RequestServices;
        var address = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        switch (policy)
        {
            // Login is limited per IP address across all accounts (TEN-12); per-account protection is the lockout in LoginService.
            case LoginEndpoints.RateLimitPolicy:
                var login = services.GetRequiredService<IOptions<LoginProtectionOptions>>().Value;
                return Rule(policy, address, login.IpLimit, login.IpWindowMinutes);

            // Asking for and using password reset links is limited per IP address.
            case PasswordResetEndpoints.RateLimitPolicy:
                var reset = services.GetRequiredService<IOptions<PasswordResetOptions>>().Value;
                return Rule(policy, address, reset.IpLimit, reset.IpWindowMinutes);

            // Service-token requests are limited per IP address across all products.
            case ServiceTokenEndpoints.RateLimitPolicy:
                var token = services.GetRequiredService<IOptions<ApplicationCredentialOptions>>().Value;
                return Rule(policy, address, token.TokenIpLimit, token.TokenIpWindowMinutes);

            // Token exchange is limited per signed-in person, so one looping client cannot starve the database for everyone else (and a
            // person is not penalised for sharing an address). Authentication runs before the limiter; a caller with no valid token is
            // limited per IP address instead.
            case TokenExchangeEndpoints.RateLimitPolicy:
                var exchange = services.GetRequiredService<IOptions<TokenExchangeOptions>>().Value;
                var who = httpContext.User.GetUserId() is { } userId ? $"user:{userId}" : $"ip:{address}";
                return Rule(policy, who, exchange.UserLimit, exchange.UserWindowMinutes);

            case EmailVerificationEndpoints.ResendRateLimitPolicy:
                var resend = services.GetRequiredService<IOptions<EmailVerificationOptions>>().Value;
                return Rule(policy, address, resend.ResendIpLimit, resend.ResendIpWindowMinutes);

            default:
                throw new InvalidOperationException($"There is no rate-limit policy called '{policy}'.");
        }
    }

    private static RateLimitRule Rule(string policy, string partition, int limit, int windowMinutes) =>
        new($"{policy}|{partition}", limit, TimeSpan.FromMinutes(windowMinutes));
}

/// <summary>
/// Counts each request to a limited endpoint in the configured store and refuses it with 429 once the limit for the window is passed.
/// After authentication, so a limit can be per person, and before authorization, as the built-in limiter was.
/// </summary>
public sealed class SharedRateLimitMiddleware(RequestDelegate next, IRateLimitStore store, ILogger<SharedRateLimitMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<SharedRateLimitMetadata>() is not { } limited)
        {
            await next(context);
            return;
        }

        var rule = RateLimitPolicies.Resolve(limited.Policy, context);

        RateLimitHit hit;
        try
        {
            hit = await store.HitAsync(rule.Key, rule.Window, context.RequestAborted);
        }
        catch (Exception exception) when (!context.RequestAborted.IsCancellationRequested)
        {
            // Never fail open: see RateLimitPolicies. The policy is logged, never the caller.
            logger.LogError(exception, "The rate-limit store could not be reached, so a request to a limited endpoint was refused ({Policy}).", limited.Policy);
            context.Response.Headers.RetryAfter = "5";
            await ApiProblem.Create(
                    StatusCodes.Status503ServiceUnavailable,
                    "rate_limit_unavailable",
                    "Temporarily unavailable",
                    "The service cannot check how often this has been tried right now. Try again shortly.")
                .ExecuteAsync(context);
            return;
        }

        if (hit.Hits > rule.Limit)
        {
            context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(hit.WindowRemaining.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            await ApiProblem.Create(StatusCodes.Status429TooManyRequests, "rate_limited", "Too many requests", "Try again later.")
                .ExecuteAsync(context);
            return;
        }

        await next(context);
    }
}

/// <summary>Removes the counters whose windows are over, on a schedule, so the store does not grow without bound.</summary>
public sealed class RateLimitCleanupWorker(IRateLimitStore store, IOptions<RateLimitOptions> options, ILogger<RateLimitCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.CleanupSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
                await store.PurgeExpiredAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // The database may be briefly unreachable. Expired counters are harmless meanwhile: a window that is over is replaced on use.
                logger.LogWarning(exception, "Removing expired rate-limit counters failed; trying again shortly.");
            }
        }
    }
}
