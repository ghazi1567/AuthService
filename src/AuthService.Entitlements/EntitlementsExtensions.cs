using System.Collections.Concurrent;
using System.Text;
using AuthService.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuthService.Entitlements;

/// <summary>Installs the entitlements package and maps its webhook endpoint.</summary>
public static class EntitlementsExtensions
{
    /// <summary>
    /// Registers <see cref="IEntitlements"/>, <see cref="IEntitlementsClient"/>, <see cref="AuthWebhookVerifier"/> and, unless one is already
    /// registered, an <see cref="InMemoryEntitlementStore"/>. The reads go through the <see cref="IAuthServiceClient"/> that
    /// <c>AddAuthServiceProduct</c> registers, so call that too.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddAuthServiceProduct(options => builder.Configuration.GetSection("AuthService").Bind(options));
    /// builder.Services.AddAuthServiceEntitlements(options => builder.Configuration.GetSection("AuthService:Entitlements").Bind(options));
    /// // AuthService__Entitlements__WebhookSecrets__0=whsec_…
    /// </code>
    /// </example>
    public static IServiceCollection AddAuthServiceEntitlements(this IServiceCollection services, Action<AuthServiceEntitlementsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        services.AddOptions<AuthServiceEntitlementsOptions>()
            .Configure(configure)
            .Validate(options => options.WebhookSecrets.Any(secret => !string.IsNullOrWhiteSpace(secret)),
                "AuthServiceEntitlementsOptions.WebhookSecrets needs the product's webhook signing secret, shown when its callback was registered.")
            .Validate(options => options.SignatureTolerance > TimeSpan.Zero && options.SignatureTolerance <= TimeSpan.FromHours(1),
                "AuthServiceEntitlementsOptions.SignatureTolerance must be more than zero and at most an hour.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IEntitlementStore, InMemoryEntitlementStore>();
        services.TryAddSingleton<IEntitlementsClient>(provider => new EntitlementsClient(provider.GetService<IAuthServiceClient>()
            ?? throw new InvalidOperationException("AuthService.Entitlements reads through IAuthServiceClient: call AddAuthServiceProduct as well.")));
        services.TryAddSingleton<IEntitlements, Entitlements>();
        services.TryAddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<AuthServiceEntitlementsOptions>>().Value;
            return new AuthWebhookVerifier(options.WebhookSecrets, provider.GetRequiredService<TimeProvider>(), options.SignatureTolerance);
        });
        services.TryAddSingleton<SeenEvents>();
        return services;
    }

    /// <summary>
    /// Maps the address the Auth Service posts its events to (register the same address as the product's callback). Each request is
    /// checked against the signing secrets (<c>401</c> if it fails), read (<c>400</c> if it isn't an event this package can read, such as a
    /// newer schema version), dropped if its event id was already handled, applied to the store (only a newer version replaces what is
    /// kept) and answered with <c>204</c> straight away. The endpoint allows anonymous calls: the signature is the check.
    /// </summary>
    public static IEndpointConventionBuilder MapAuthServiceWebhook(this IEndpointRouteBuilder endpoints, string pattern)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        return endpoints.MapPost(pattern, (Delegate)HandleAsync).AllowAnonymous().DisableAntiforgery().WithDisplayName("Auth Service webhook");
    }

    private static async Task<IResult> HandleAsync(HttpContext context)
    {
        var services = context.RequestServices;
        var options = services.GetRequiredService<IOptions<AuthServiceEntitlementsOptions>>().Value;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("AuthService.Entitlements.Webhook");
        var cancellationToken = context.RequestAborted;

        var body = await ReadBodyAsync(context.Request, options.MaxBodyBytes, cancellationToken);
        if (body is null)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var verifier = services.GetRequiredService<AuthWebhookVerifier>();
        if (!verifier.Verify(context.Request.Headers[AuthWebhookVerifier.TimestampHeader], context.Request.Headers[AuthWebhookVerifier.SignatureHeader], body))
        {
            logger.LogWarning("An Auth Service webhook was refused: its signature or time did not check out.");
            return Results.Unauthorized();
        }

        AuthWebhookEvent received;
        try
        {
            received = AuthWebhookParser.Parse(body);
        }
        catch (AuthWebhookException exception)
        {
            logger.LogWarning("An Auth Service webhook could not be read: {Reason}", exception.Message);
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Event not readable", detail: exception.Message);
        }

        var seen = services.GetRequiredService<SeenEvents>();
        if (!seen.TryStart(received.EventId))
        {
            return Results.NoContent();
        }

        try
        {
            var store = services.GetRequiredService<IEntitlementStore>();
            switch (received)
            {
                case EntitlementsChangedEvent changed:
                    await store.ApplyAsync(changed.Entitlements, cancellationToken);
                    break;
                case ApplicationSubscribedEvent { Entitlements: { } snapshot }:
                    await store.ApplyAsync(snapshot, cancellationToken);
                    await store.SetStatusAsync(snapshot.TenantId, "Active", cancellationToken);
                    break;
                case ApplicationSubscribedEvent subscribed:
                    await store.SetStatusAsync(subscribed.TenantId, "Active", cancellationToken);
                    break;
                case ApplicationUnsubscribedEvent unsubscribed:
                    await store.SetStatusAsync(unsubscribed.TenantId, unsubscribed.Status, cancellationToken);
                    break;
            }

            if (options.OnEvent is { } onEvent)
            {
                await onEvent(received, services, cancellationToken);
            }

            seen.Finish(received.EventId);
            return Results.NoContent();
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Not handled: forget the id so the Auth Service's next attempt is processed, and answer with an error so it makes one.
            seen.Forget(received.EventId);
            logger.LogError(exception, "Handling the Auth Service event {EventId} ({EventType}) failed; it will be sent again.", received.EventId, received.EventType);
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<string?> ReadBodyAsync(HttpRequest request, int maxBytes, CancellationToken cancellationToken)
    {
        if (request.ContentLength > maxBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}

/// <summary>The event ids handled recently, to drop a second delivery. One instance only; a product on several instances relies on versions.</summary>
internal sealed class SeenEvents(IOptions<AuthServiceEntitlementsOptions> options, TimeProvider time)
{
    private const int PruneAbove = 10_000;
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _seen = new();

    /// <summary>Claims the event; false when it was already handled or is being handled now.</summary>
    public bool TryStart(Guid eventId)
    {
        var now = time.GetUtcNow();
        if (_seen.Count > PruneAbove)
        {
            foreach (var (id, at) in _seen)
            {
                if (now - at > options.Value.DuplicateWindow)
                {
                    _seen.TryRemove(id, out _);
                }
            }
        }

        if (_seen.TryGetValue(eventId, out var handledAt) && now - handledAt > options.Value.DuplicateWindow)
        {
            _seen.TryRemove(eventId, out _);
        }

        return _seen.TryAdd(eventId, now);
    }

    public void Finish(Guid eventId) => _seen[eventId] = time.GetUtcNow();

    public void Forget(Guid eventId) => _seen.TryRemove(eventId, out _);
}
