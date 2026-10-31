using System.Net;
using System.Text;
using System.Text.Json;
using AuthService.Api.Email;
using AuthService.Api.Email.Providers;
using Microsoft.Extensions.Options;

namespace AuthService.IntegrationTests.Ntf01;

/// <summary>
/// The Brevo provider against a stub HTTP handler: no network, no account, but it checks exactly what would be sent.
/// These tests have no workbook Test Case ID yet: the spec's acceptance criteria cover them.
/// </summary>
public sealed class BrevoEmailSenderTests
{
    private static readonly EmailMessage Message = new("owner@acme.com", "Verify", "<p>html</p>", "text");

    private static BrevoEmailSender CreateSender(StubHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.brevo.com/") },
            Options.Create(new EmailOptions { BrevoApiKey = "key-123", FromAddress = "no-reply@acme.com", FromName = "Acme" }));

    [Fact]
    public async Task NTF01_Brevo_SendsTheEmailInBrevosTransactionalFormat()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));

        await CreateSender(handler).SendAsync(Message, ct);

        var request = handler.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        request.Uri.ToString().ShouldBe("https://api.brevo.com/v3/smtp/email");
        request.ApiKey.ShouldBe("key-123");

        using var body = JsonDocument.Parse(request.Body);
        body.RootElement.GetProperty("sender").GetProperty("email").GetString().ShouldBe("no-reply@acme.com");
        body.RootElement.GetProperty("sender").GetProperty("name").GetString().ShouldBe("Acme");
        body.RootElement.GetProperty("to")[0].GetProperty("email").GetString().ShouldBe("owner@acme.com");
        body.RootElement.GetProperty("subject").GetString().ShouldBe("Verify");
        body.RootElement.GetProperty("htmlContent").GetString().ShouldBe("<p>html</p>");
        body.RootElement.GetProperty("textContent").GetString().ShouldBe("text");
    }

    [Fact]
    public async Task NTF01_Brevo_ARefusalIsReportedWithOnlyItsStatusCode()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("secret-details-from-brevo"),
        });

        var failure = await Should.ThrowAsync<EmailSendException>(CreateSender(handler).SendAsync(Message, ct));

        failure.Message.ShouldBe("Brevo returned 401.");
        failure.ToString().ShouldNotContain("secret-details-from-brevo");
    }

    [Fact]
    public async Task NTF01_Brevo_ANetworkFailureIsReportedWithoutDetails()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHandler(_ => throw new HttpRequestException("connection details with api-key=key-123"));

        var failure = await Should.ThrowAsync<EmailSendException>(CreateSender(handler).SendAsync(Message, ct));

        failure.Message.ShouldBe("Brevo could not be reached.");
        failure.ToString().ShouldNotContain("key-123");
    }

    private sealed record CapturedRequest(HttpMethod Method, Uri Uri, string? ApiKey, string Body);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly List<CapturedRequest> _requests = [];

        public IReadOnlyList<CapturedRequest> Requests => _requests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            _requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.TryGetValues("api-key", out var values) ? values.Single() : null,
                body));

            return respond(request);
        }
    }
}
