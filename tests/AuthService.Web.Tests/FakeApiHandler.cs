using System.Net;
using System.Text;

namespace AuthService.Web.Tests;

/// <summary>A stand-in for the Auth Service API: answers what the test says, and remembers what it was asked.</summary>
public sealed class FakeApiHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    public static FakeApiHandler Json(HttpStatusCode status, string body, string contentType = "application/problem+json") =>
        new(_ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) });

    public static FakeApiHandler Throwing(Exception exception) => new(_ => throw exception);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(respond(request));
    }
}
