using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AuthService.Entitlements.Testing;

namespace SampleProduct.Tests;

/// <summary>
/// The sample product with <c>AuthService.Entitlements</c> installed from a feed (ENT-07): the Auth Service's signed event sets an
/// organisation's limit, and the product refuses an import that would go past it. The events are made by the package's own test kit,
/// signed with the secret in the sample's settings, so no Auth Service needs to run.
/// </summary>
public sealed partial class SampleProductTests
{
    private const string WebhookSecret = "replace-with-the-webhook-signing-secret";

    [Fact]
    public void TheEntitlementsPackagesAreInstalledPackagesNotProjects()
    {
        var depsFile = Path.Combine(AppContext.BaseDirectory, "SampleProduct.Tests.deps.json");
        using var deps = JsonDocument.Parse(File.ReadAllText(depsFile));
        var libraries = deps.RootElement.GetProperty("libraries").EnumerateObject().ToList();

        foreach (var package in new[] { "AuthService.Entitlements", "AuthService.Entitlements.Testing" })
        {
            var installed = libraries.Where(library => library.Name.StartsWith(package + "/", StringComparison.Ordinal)).ShouldHaveSingleItem();
            installed.Value.GetProperty("type").GetString().ShouldBe("package");
            installed.Name.ShouldBe($"{package}/{ExpectedEntitlementsVersion}");
        }
    }

    [Fact]
    public async Task ASignedEventSetsTheOrganisationsLimitAndAnImportPastItIsRefused()
    {
        var tenant = Guid.NewGuid();
        var auth = new FakeAuthService().AddLimit("contacts.reachable", "Reachable contacts", 1_000, "contacts");
        auth.SetTenant(tenant, new Dictionary<string, long?> { ["contacts.reachable"] = 100 }, planKey: "starter", planName: "Starter");
        var token = Token(tenant, ["campaign.create:CreateRead"]);

        var delivered = await _client.SendAsync(SignedWebhook.Create("/auth-webhooks", auth.EntitlementsChangedEvent(tenant), WebhookSecret), TestContext.Current.CancellationToken);
        var first = await ImportAsync(60, token);
        var tooMany = await ImportAsync(50, token);
        var rest = await ImportAsync(40, token);

        delivered.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await first.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("remaining").GetInt64().ShouldBe(40);
        tooMany.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await tooMany.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("code").GetString().ShouldBe("limit_reached");
        rest.StatusCode.ShouldBe(HttpStatusCode.OK, "exactly up to the limit is allowed");
    }

    [Fact]
    public async Task AnEventWithoutTheRightSignatureIsRefused()
    {
        var tenant = Guid.NewGuid();
        var auth = new FakeAuthService().AddLimit("contacts.reachable", "Reachable contacts", 1_000, "contacts");
        auth.SetTenant(tenant, new Dictionary<string, long?> { ["contacts.reachable"] = 5 });

        var response = await _client.SendAsync(SignedWebhook.Create("/auth-webhooks", auth.EntitlementsChangedEvent(tenant), "not-the-secret"), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static string ExpectedEntitlementsVersion =>
        typeof(SampleProductTests).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "EntitlementsPackageVersion").Value!;

    private Task<HttpResponseMessage> ImportAsync(long count, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/contacts/import") { Content = JsonContent.Create(new { count }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
