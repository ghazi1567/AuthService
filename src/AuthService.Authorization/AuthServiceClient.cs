using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AuthService.Authorization;

/// <summary>The product's calls to the Auth Service as itself (<see cref="IAuthServiceClient"/>), through the one <see cref="ProductConnection"/>.</summary>
internal sealed class AuthServiceClient(ProductConnection connection) : IAuthServiceClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<string> GetServiceTokenAsync(CancellationToken cancellationToken = default) => connection.GetServiceTokenAsync(cancellationToken);

    public async Task<AuthServiceProduct> GetProductAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, connection.Resolve("/api/auth/applications/me")), cancellationToken);
        var record = await ReadAsync<ProductBody>(response, cancellationToken);
        return new AuthServiceProduct(
            record.Id, record.Key ?? "", record.Name ?? "", record.JwtAudience ?? "", record.ClientId ?? "", record.IsActive, record.PermissionKeys ?? []);
    }

    public async Task<AuthServiceSubscription> SubscribeTenantAsync(Guid tenantId, int? maxSeats = null, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, connection.Resolve($"/api/auth/tenants/{tenantId}/subscriptions"))
            {
                Content = JsonContent.Create(new { maxSeats }, options: Json),
            },
            cancellationToken);
        var subscription = await ReadAsync<SubscriptionBody>(response, cancellationToken);
        return new AuthServiceSubscription(subscription.TenantId, subscription.ApplicationId, subscription.Status ?? "", subscription.MaxSeats, subscription.SeatsUsed);
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, CancellationToken cancellationToken)
    {
        try
        {
            return await connection.SendAsync(build, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new AuthServiceException(null, null, "The Auth Service could not be reached.", exception);
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await ProductConnection.ProblemAsync(response, cancellationToken);
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
                ?? throw new AuthServiceException(response.StatusCode, null, "The Auth Service sent an empty answer.");
        }
        catch (JsonException exception)
        {
            throw new AuthServiceException(response.StatusCode, null, "The Auth Service sent an answer that could not be read.", exception);
        }
    }

    private sealed record ProductBody(Guid Id, string? Key, string? Name, string? JwtAudience, string? ClientId, bool IsActive, List<string>? PermissionKeys);

    private sealed record SubscriptionBody(Guid TenantId, Guid ApplicationId, string? Status, int? MaxSeats, int SeatsUsed);
}
