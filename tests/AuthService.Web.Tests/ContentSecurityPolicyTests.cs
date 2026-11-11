using System.Net;
using System.Text.RegularExpressions;

namespace AuthService.Web.Tests;

/// <summary>
/// AUTH-271 (FIX-06) The console's content security policy restricts scripts to this site's files and inline scripts carrying the
/// response's nonce. No page needs <c>unsafe-inline</c>: every inline script on every page has the nonce. A setting reports the script
/// policy instead of enforcing it, for a first rollout, while the rest stays enforced.
/// </summary>
public sealed partial class ContentSecurityPolicyTests
{
    private static readonly Guid Acme = Guid.Parse("7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11");
    private static readonly Guid Marketing = Guid.Parse("20000000-0000-4000-8000-000000000005");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/login")]
    [InlineData("/signup")]
    [InlineData("/forgot-password")]
    [InlineData("/no-such-page")]
    public async Task AUTH271_PublicPagesSendTheScriptPolicyAndEveryInlineScriptHasItsNonce(string path)
    {
        await using var factory = new WebAppFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path, Token);

        AssertPolicy(response, await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task AUTH271_SignedInPickerConsoleAndPlatformPagesDoToo()
    {
        await using var factory = new WebAppFactory(PlatformLimitsPageTests.Api());
        using var client = await PlatformLimitsPageTests.SignedInAsync(factory);

        foreach (var path in new[]
        {
            "/choose-organisation?change=1",
            $"/console/{Acme}/subscriptions",
            "/platform/products",
            $"/platform/products/{Marketing}/limits?add=1",
            "/platform/entitlement-history",
        })
        {
            var response = await client.GetAsync(path, Token);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, path);
            AssertPolicy(response, await response.Content.ReadAsStringAsync(Token), path);
        }
    }

    [Fact]
    public async Task AUTH271_EveryResponseHasANewNonce()
    {
        await using var factory = new WebAppFactory();
        using var client = factory.CreateClient();

        var first = NonceOf(await client.GetAsync("/login", Token));
        var second = NonceOf(await client.GetAsync("/login", Token));

        first.ShouldNotBe(second);
        first.Length.ShouldBeGreaterThanOrEqualTo(24, "18 random bytes, base64");
    }

    [Fact]
    public async Task AUTH271_ReportOnlyReportsTheScriptPolicyAndStillEnforcesTheRest()
    {
        await using var factory = new WebAppFactory(settings: new Dictionary<string, string> { ["Security:ScriptPolicyReportOnly"] = "true" });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/login", Token);

        var enforced = response.Headers.GetValues("Content-Security-Policy").Single();
        enforced.ShouldContain("frame-ancestors 'none'");
        enforced.ShouldContain("object-src 'none'");
        enforced.ShouldContain("base-uri 'self'");
        enforced.ShouldNotContain("script-src");
        response.Headers.GetValues("Content-Security-Policy-Report-Only").Single().ShouldStartWith("script-src 'self' 'nonce-");
    }

    private static void AssertPolicy(HttpResponseMessage response, string html, string? because = null)
    {
        var policy = response.Headers.GetValues("Content-Security-Policy").Single();
        policy.ShouldContain("frame-ancestors 'none'", customMessage: because);
        policy.ShouldContain("object-src 'none'", customMessage: because);
        policy.ShouldContain("base-uri 'self'", customMessage: because);
        var nonce = NonceOf(response);
        var scriptSource = policy.Split(';', StringSplitOptions.TrimEntries).Single(directive => directive.StartsWith("script-src", StringComparison.Ordinal));
        scriptSource.ShouldBe($"script-src 'self' 'nonce-{nonce}'", because);
        scriptSource.ShouldNotContain("unsafe-inline");
        scriptSource.ShouldNotContain("unsafe-eval");
        response.Headers.Contains("Content-Security-Policy-Report-Only").ShouldBeFalse(because);

        // Every script without a src is inline and must carry this response's nonce, or the browser would block it.
        var inline = ScriptTag().Matches(html).Select(match => match.Value).Where(tag => !tag.Contains(" src=", StringComparison.Ordinal)).ToList();
        // In a published app the import map is one of them; the test host has no asset manifest, so it may render none here.
        inline.ShouldAllBe(tag => tag.Contains($"nonce=\"{nonce}\""), because);
        html.ShouldNotContain(" onclick=", Case.Insensitive, because);
        html.ShouldNotContain("javascript:", Case.Insensitive, because);
    }

    internal static string NonceOf(HttpResponseMessage response) =>
        NonceInPolicy().Match(response.Headers.GetValues("Content-Security-Policy").Single()).Groups[1].Value;

    [GeneratedRegex("<script\\b[^>]*>")]
    private static partial Regex ScriptTag();

    [GeneratedRegex("'nonce-([^']+)'")]
    private static partial Regex NonceInPolicy();
}
