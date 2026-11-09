using System.Net;

namespace AuthService.Authorization;

/// <summary>The product's own record at the Auth Service, as it reads it with its client secret.</summary>
/// <param name="Id">The product's id.</param>
/// <param name="Key">The product's key.</param>
/// <param name="Name">The product's display name.</param>
/// <param name="Audience">The audience of the product's tokens: its own.</param>
/// <param name="ClientId">The product's client id.</param>
/// <param name="IsActive">Whether the product is available, so organisations can subscribe to it.</param>
/// <param name="PermissionKeys">The keys of the product's permission catalogue, in order.</param>
public sealed record AuthServiceProduct(
    Guid Id,
    string Key,
    string Name,
    string Audience,
    string ClientId,
    bool IsActive,
    IReadOnlyList<string> PermissionKeys);

/// <summary>An organisation's subscription to the product.</summary>
/// <param name="TenantId">The organisation.</param>
/// <param name="ApplicationId">The product.</param>
/// <param name="Status"><c>Active</c>, <c>Suspended</c> or <c>Cancelled</c>.</param>
/// <param name="MaxSeats">How many people can have access, or null for no limit.</param>
/// <param name="SeatsUsed">How many seats are in use.</param>
public sealed record AuthServiceSubscription(Guid TenantId, Guid ApplicationId, string Status, int? MaxSeats, int SeatsUsed);

/// <summary>The Auth Service refused a call, or could not be reached for it. Never carries the client secret or a token.</summary>
public sealed class AuthServiceException : Exception
{
    /// <summary>Creates the exception for a refusal (with the status and, if given, the code) or for an Auth Service that could not be reached (no status).</summary>
    public AuthServiceException(HttpStatusCode? statusCode, string? code, string message, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        Code = code;
    }

    /// <summary>The HTTP status the Auth Service answered with, or null if it could not be reached.</summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>The Auth Service's own name for the reason, such as <c>tenant_not_found</c>, when it gave one.</summary>
    public string? Code { get; }
}

/// <summary>
/// The calls a product makes to the Auth Service as itself, with its client secret. The service token these calls need is asked for
/// when it is first needed, kept until shortly before it expires, and asked for again once if the Auth Service turns it down. Install it
/// with <c>AddAuthServiceProduct</c>.
/// </summary>
public interface IAuthServiceClient
{
    /// <summary>A service token for the Auth Service's own endpoints, such as to call one the package has no method for yet.</summary>
    Task<string> GetServiceTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads the product's own record: its audience, whether it is available and its permission keys.</summary>
    Task<AuthServiceProduct> GetProductAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribes an organisation to this product, typically from the product's own sign-up or checkout. Subscribing one that is already
    /// active changes nothing. <paramref name="maxSeats"/> null means no limit.
    /// </summary>
    Task<AuthServiceSubscription> SubscribeTenantAsync(Guid tenantId, int? maxSeats = null, CancellationToken cancellationToken = default);
}
