using AuthService.Web.Components.Layout;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AuthService.Web.Tests;

/// <summary>The three page shells: each has the landmarks and the skip link the accessibility baseline (UI00_T6) starts from.</summary>
public sealed class LayoutTests
{
    [Theory]
    [InlineData(typeof(MainLayout))]
    [InlineData(typeof(AuthLayout))]
    [InlineData(typeof(TenantLayout))]
    [InlineData(typeof(PlatformLayout))]
    [InlineData(typeof(PickerLayout))]
    public async Task EveryShellHasASkipLinkAMainLandmarkAndTheErrorBar(Type layout)
    {
        var html = await RenderAsync(layout);

        html.ShouldContain("""<a class="skip-link" href="#main" """.TrimEnd());
        html.ShouldContain("Skip to content</a>");
        html.ShouldContain("""<main id="main" tabindex="-1" """.TrimEnd());
        html.ShouldContain("<p>page content</p>", customMessage: "the page is rendered inside the shell");
        html.ShouldContain("""id="blazor-error-ui" """.TrimEnd(), customMessage: "an unhandled error is announced");
        html.ShouldContain("""role="alert" """.TrimEnd());
    }

    [Fact]
    public async Task TheConsolesHaveAHeaderAndNavigationAndThePlatformOneSaysWhereYouAre()
    {
        var tenant = await RenderAsync(typeof(TenantLayout));
        var platform = await RenderAsync(typeof(PlatformLayout));

        tenant.ShouldContain("<header");
        tenant.ShouldContain("""<nav aria-label="Main" """.TrimEnd());
        platform.ShouldContain("<header");
        platform.ShouldContain("""<nav aria-label="Main" """.TrimEnd());
        platform.ShouldContain("Platform administration");
        tenant.ShouldNotContain("Platform administration");
    }

    private static async Task<string> RenderAsync(Type layout)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment body = builder => builder.AddMarkupContent(0, "<p>page content</p>");
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?> { ["Body"] = body });
            var output = await renderer.RenderComponentAsync(layout, parameters);
            return output.ToHtmlString();
        });
    }
}
