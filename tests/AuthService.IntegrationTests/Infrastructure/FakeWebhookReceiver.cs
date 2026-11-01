using System.Collections.Concurrent;
using System.Net;

namespace AuthService.IntegrationTests.Infrastructure;

/// <summary>
/// Stands in for a product's server: records every webhook the service posts, with the time on the test clock, and answers as the
/// test says. Registered as the primary handler of the service's webhook HTTP client.
/// </summary>
public sealed class FakeWebhookReceiver(TimeProvider clock) : HttpMessageHandler
{
    public sealed record Received(Uri Url, string Body, IReadOnlyDictionary<string, string> Headers, DateTimeOffset At);

    private readonly ConcurrentQueue<Received> _received = new();

    /// <summary>How the "product" answers. Replace it to make the endpoint fail, redirect, or throw.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

    public IReadOnlyList<Received> Requests => [.. _received];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        _received.Enqueue(new Received(request.RequestUri!, body, headers, clock.GetUtcNow()));

        return Respond(request);
    }
}
