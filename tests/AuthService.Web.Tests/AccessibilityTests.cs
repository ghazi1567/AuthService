using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace AuthService.Web.Tests;

/// <summary>
/// AUTH-213 (UI00_T6) The accessibility baseline, checked automatically on every main screen: the page has a language and a heading, every
/// control and button has a name a screen reader can read, every reference (label for, aria-labelledby, aria-describedby) points at something
/// that exists, ids are unique, dialogs say they are dialogs, icons are hidden from screen readers, nothing jumps the tab order. And the design
/// tokens meet WCAG 2.2 AA contrast in light and dark. Colour and focus that need a real browser are covered by the manual keyboard run.
/// </summary>
public sealed partial class AccessibilityTests
{
    private static readonly Guid Acme = Guid.Parse("7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11");
    private static readonly Guid Marketing = Guid.Parse("20000000-0000-4000-8000-000000000005");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<string> PublicPages =>
    [
        "/login",
        "/signup",
        "/forgot-password",
        "/reset-password",
        "/verify-email?result=invalid",
        "/accept-invitation",
        "/no-such-page",
    ];

    public static TheoryData<string> SignedInPages =>
    [
        "/choose-organisation?change=1",
        $"/choose-product?tenantId={Acme}&stay=1",
        $"/console/{Acme}/members",
        $"/console/{Acme}/invitations",
        $"/console/{Acme}/invitations?invite=1",
        $"/console/{Acme}/roles",
        $"/console/{Acme}/roles/new",
        $"/console/{Acme}/access",
        $"/console/{Acme}/api-keys",
        $"/console/{Acme}/api-keys?create=1",
        $"/console/{Acme}/subscriptions",
        $"/console/{Acme}/audit",
        "/platform/products",
        "/platform/products?register=1",
        "/platform/organisations",
        "/platform/webhooks",
        "/platform/signing-keys",
        "/platform/audit",
        "/platform/entitlement-history",
        $"/platform/products/{Marketing}/permissions",
        $"/platform/products/{Marketing}/limits?add=1",
        $"/platform/products/{Marketing}/plans",
        $"/platform/products/{Marketing}/plans/new",
    ];

    [Theory]
    [MemberData(nameof(PublicPages))]
    public async Task UI00_T6_APublicPageMeetsTheBaseline(string path)
    {
        await using var factory = new WebAppFactory();
        using var client = factory.CreateClient();

        // The not-found page answers 404 on purpose; it still has to meet the baseline.
        var html = await (await client.GetAsync(path, Token)).Content.ReadAsStringAsync(Token);

        Check(path, html).ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(SignedInPages))]
    public async Task UI00_T6_ASignedInPageMeetsTheBaseline(string path)
    {
        await using var factory = new WebAppFactory(PlatformLimitsPageTests.Api());
        using var client = await PlatformLimitsPageTests.SignedInAsync(factory);

        var response = await client.GetAsync(path, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, path);
        Check(path, await response.Content.ReadAsStringAsync(Token)).ShouldBeEmpty();
    }

    [Fact]
    public void UI00_T6_TheCheckFindsTheProblemsItLooksFor()
    {
        const string broken = """
            <html><body>
              <input id="email" type="email"><input id="email" type="text" aria-describedby="missing">
              <button></button><a href="/x"></a><img src="x.png"><svg></svg>
              <div role="dialog"></div><span tabindex="3">x</span>
            </body></html>
            """;

        var problems = Check("/broken", broken);

        problems.ShouldContain(problem => problem.Contains("no lang", StringComparison.Ordinal));
        problems.ShouldContain(problem => problem.Contains("no h1", StringComparison.Ordinal));
        problems.ShouldContain(problem => problem.Contains("id 'email' is used 2 times", StringComparison.Ordinal));
        problems.ShouldContain(problem => problem.Contains("input#email has no label", StringComparison.Ordinal));
        problems.ShouldContain(problem => problem.Contains("'missing', which does not exist", StringComparison.Ordinal));
        problems.ShouldContain(problem => problem.Contains("button with no name", StringComparison.Ordinal));
        problems.ShouldContain(problem => problem.Contains("link with no name", StringComparison.Ordinal));
        problems.ShouldContain(problem => problem.Contains("img with no alt", StringComparison.Ordinal));
        problems.ShouldContain(problem => problem.Contains("svg not hidden", StringComparison.Ordinal));
        problems.ShouldContain(problem => problem.Contains("dialog without aria-modal", StringComparison.Ordinal));
        problems.ShouldContain(problem => problem.Contains("tabindex 3", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public void UI00_T6_TheDesignTokensMeetAaContrast(string theme)
    {
        var tokens = Tokens(theme);

        // Text: 4.5 to 1 against the backgrounds it is used on (soft and tinted backgrounds are blended over the surface first).
        var problems = new List<string>();
        foreach (var (text, background) in new[]
        {
            ("--text-primary", "--bg-canvas"), ("--text-primary", "--bg-surface"), ("--text-primary", "--bg-sunken"), ("--text-primary", "--bg-raised"),
            ("--text-secondary", "--bg-canvas"), ("--text-secondary", "--bg-surface"),
            ("--text-tertiary", "--bg-canvas"), ("--text-tertiary", "--bg-surface"),
            ("--text-link", "--bg-surface"), ("--text-brand", "--bg-surface"), ("--text-brand", "--action-primary-soft"),
            ("--text-on-primary", "--action-primary"), ("--text-on-primary", "--action-primary-hover"),
            ("--success-text", "--success-bg"), ("--warning-text", "--warning-bg"), ("--error-text", "--error-bg"), ("--info-text", "--info-bg"),
            ("--platform-header-text", "--platform-header"), ("--platform-header-muted", "--platform-header"),
        })
        {
            Require(problems, tokens, text, background, 4.5);
        }

        // Controls and focus: 3 to 1 (WCAG 1.4.11).
        Require(problems, tokens, "--border-control", "--bg-surface", 3);
        Require(problems, tokens, "--focus-ring", "--bg-surface", 3);
        Require(problems, tokens, "--focus-ring", "--bg-canvas", 3);

        problems.ShouldBeEmpty();
    }

    /// <summary>Every accessibility problem found on one page, as readable sentences; empty when the page meets the baseline.</summary>
    internal static List<string> Check(string path, string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        var all = document.DocumentNode.Descendants().Where(node => node.NodeType == HtmlNodeType.Element).ToList();
        var problems = new List<string>();
        void Problem(string message) => problems.Add($"{path}: {message}");
        IEnumerable<HtmlNode> Named(params string[] names) => all.Where(node => names.Contains(node.Name));

        var root = Named("html").FirstOrDefault();
        if (string.IsNullOrWhiteSpace(root?.GetAttributeValue("lang", "")))
        {
            Problem("the page has no lang");
        }

        if (!Named("h1").Any())
        {
            Problem("the page has no h1");
        }

        var ids = all.Select(node => node.GetAttributeValue("id", "")).Where(id => id.Length > 0).ToList();
        foreach (var duplicate in ids.GroupBy(id => id).Where(group => group.Count() > 1))
        {
            Problem($"id '{duplicate.Key}' is used {duplicate.Count()} times");
        }

        var known = ids.ToHashSet(StringComparer.Ordinal);
        foreach (var element in all)
        {
            foreach (var attribute in new[] { "aria-labelledby", "aria-describedby", "for" })
            {
                if (attribute == "for" && element.Name != "label")
                {
                    continue;
                }

                foreach (var reference in element.GetAttributeValue(attribute, "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!known.Contains(reference))
                    {
                        Problem($"{Describe(element)} has {attribute} '{reference}', which does not exist");
                    }
                }
            }
        }

        var labelledIds = Named("label").Select(label => label.GetAttributeValue("for", "")).Where(id => id.Length > 0).ToHashSet(StringComparer.Ordinal);
        foreach (var control in Named("input", "select", "textarea"))
        {
            var type = control.GetAttributeValue("type", "").ToLowerInvariant();
            if (type is "hidden" or "submit" or "button" or "reset" or "image")
            {
                continue;
            }

            var id = control.GetAttributeValue("id", "");
            var labelled = !string.IsNullOrWhiteSpace(control.GetAttributeValue("aria-label", ""))
                || !string.IsNullOrWhiteSpace(control.GetAttributeValue("aria-labelledby", ""))
                || control.Ancestors("label").Any()
                || (id.Length > 0 && labelledIds.Contains(id));
            if (!labelled)
            {
                Problem($"{Describe(control)} has no label");
            }
        }

        foreach (var button in Named("button").Where(button => !HasName(button)))
        {
            Problem($"{Describe(button)}: a button with no name");
        }

        foreach (var link in Named("a").Where(link => link.Attributes.Contains("href") && !HasName(link)))
        {
            Problem($"{Describe(link)}: a link with no name");
        }

        foreach (var image in Named("img").Where(image => !image.Attributes.Contains("alt")))
        {
            Problem($"{Describe(image)}: an img with no alt");
        }

        foreach (var icon in Named("svg"))
        {
            var hidden = icon.GetAttributeValue("aria-hidden", "") == "true" || icon.Ancestors().Any(parent => parent.GetAttributeValue("aria-hidden", "") == "true");
            var named = icon.GetAttributeValue("role", "") == "img"
                && (!string.IsNullOrWhiteSpace(icon.GetAttributeValue("aria-label", "")) || icon.Descendants("title").Any());
            if (!hidden && !named)
            {
                Problem($"{Describe(icon)}: an svg not hidden from screen readers and without a name");
            }
        }

        foreach (var dialog in all.Where(node => node.Name == "dialog" || node.GetAttributeValue("role", "") is "dialog" or "alertdialog"))
        {
            if (dialog.Name != "dialog" && dialog.GetAttributeValue("aria-modal", "") != "true")
            {
                Problem($"{Describe(dialog)}: a dialog without aria-modal");
            }

            if (string.IsNullOrWhiteSpace(dialog.GetAttributeValue("aria-labelledby", "")) && string.IsNullOrWhiteSpace(dialog.GetAttributeValue("aria-label", "")))
            {
                Problem($"{Describe(dialog)}: a dialog without a title (aria-labelledby)");
            }
        }

        foreach (var jumper in all.Where(node => node.Attributes.Contains("tabindex")))
        {
            if (int.TryParse(jumper.GetAttributeValue("tabindex", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index > 0)
            {
                Problem($"{Describe(jumper)} has tabindex {index}, which jumps the tab order");
            }
        }

        return problems;
    }

    /// <summary>Whether a screen reader can read a name for this element: its text (not counting hidden icons), aria-label, aria-labelledby or title.</summary>
    private static bool HasName(HtmlNode element) =>
        !string.IsNullOrWhiteSpace(element.GetAttributeValue("aria-label", ""))
        || !string.IsNullOrWhiteSpace(element.GetAttributeValue("aria-labelledby", ""))
        || !string.IsNullOrWhiteSpace(element.GetAttributeValue("title", ""))
        || !string.IsNullOrWhiteSpace(ReadableText(element));

    /// <summary>The text a screen reader reads inside a node: icons and anything marked aria-hidden left out.</summary>
    private static string ReadableText(HtmlNode node) => node.NodeType switch
    {
        HtmlNodeType.Text => WebUtility.HtmlDecode(node.InnerText),
        HtmlNodeType.Element when node.Name == "svg" || node.GetAttributeValue("aria-hidden", "") == "true" => "",
        _ => string.Concat(node.ChildNodes.Select(ReadableText)),
    };

    private static string Describe(HtmlNode element) =>
        element.Name + (element.GetAttributeValue("id", "") is { Length: > 0 } id ? $"#{id}" : "")
        + (element.GetAttributeValue("name", "") is { Length: > 0 } name ? $"[name={name}]" : "")
        + (element.GetAttributeValue("href", "") is { Length: > 0 } href ? $"[href={href}]" : "");

    private static void Require(List<string> problems, IReadOnlyDictionary<string, string> tokens, string foreground, string background, double minimum)
    {
        var surface = Colour.Parse(tokens["--bg-surface"]);
        var back = Colour.Parse(tokens[background]).Over(surface);
        var fore = Colour.Parse(tokens[foreground]).Over(back);
        var ratio = Colour.Contrast(fore, back);
        if (ratio < minimum)
        {
            problems.Add($"{foreground} on {background}: {ratio:0.00} to 1, needs {minimum}");
        }
    }

    /// <summary>The colour tokens of one theme from app.css: the light defaults, with the dark block laid over them for "dark".</summary>
    private static Dictionary<string, string> Tokens(string theme)
    {
        var css = File.ReadAllText(FindAppCss());
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        void Read(string block)
        {
            foreach (Match declaration in Declaration().Matches(block))
            {
                tokens[declaration.Groups["name"].Value] = declaration.Groups["value"].Value.Trim();
            }
        }

        Read(Block(css, ":root {"));
        if (theme == "dark")
        {
            Read(Block(css, ":root[data-theme=\"dark\"] {"));
        }

        return tokens;
    }

    private static string Block(string css, string selector)
    {
        var start = css.IndexOf(selector, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"app.css has a '{selector}' block");
        var end = css.IndexOf('}', start);
        return css[(start + selector.Length)..end];
    }

    private static string FindAppCss() => Path.Combine(AppContext.BaseDirectory, "design-tokens", "app.css");

    [GeneratedRegex(@"(?<name>--[a-z0-9-]+)\s*:\s*(?<value>#[0-9A-Fa-f]{3,8}|rgba?\([^)]*\))\s*;")]
    private static partial Regex Declaration();

    private readonly record struct Colour(double R, double G, double B, double A)
    {
        public static Colour Parse(string value)
        {
            if (value.StartsWith('#'))
            {
                var hex = value[1..];
                if (hex.Length == 3)
                {
                    hex = string.Concat(hex.Select(digit => $"{digit}{digit}"));
                }

                return new Colour(Convert.ToInt32(hex[..2], 16), Convert.ToInt32(hex[2..4], 16), Convert.ToInt32(hex[4..6], 16), 1);
            }

            var parts = value[(value.IndexOf('(') + 1)..value.IndexOf(')')].Split(',').Select(part => double.Parse(part.Trim(), CultureInfo.InvariantCulture)).ToArray();
            return new Colour(parts[0], parts[1], parts[2], parts.Length > 3 ? parts[3] : 1);
        }

        /// <summary>This colour painted over <paramref name="below"/>, which is opaque.</summary>
        public Colour Over(Colour below) => new(R * A + below.R * (1 - A), G * A + below.G * (1 - A), B * A + below.B * (1 - A), 1);

        public static double Contrast(Colour first, Colour second)
        {
            var (light, dark) = (Luminance(first), Luminance(second)) is var (a, b) && a > b ? (a, b) : (Luminance(second), Luminance(first));
            return (light + 0.05) / (dark + 0.05);
        }

        private static double Luminance(Colour colour)
        {
            static double Channel(double value)
            {
                var c = value / 255;
                return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Channel(colour.R) + 0.7152 * Channel(colour.G) + 0.0722 * Channel(colour.B);
        }
    }
}
