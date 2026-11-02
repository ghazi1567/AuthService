using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AuthService.Web.Tests;

/// <summary>A clock the test moves by hand.</summary>
public sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

public static class TestTokens
{
    /// <summary>A product token shaped like the API's: the person, the organisation and the permissions as key:Level strings.</summary>
    public static string ProductJwt(Guid userId, Guid tenantId, IEnumerable<string> permissions, int number)
    {
        static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var list = string.Join(",", permissions.Select(permission => $"\"{permission}\""));
        return $"{Part("""{"alg":"none"}""")}.{Part($$"""{"sub":"{{userId}}","tid":"{{tenantId}}","permissions":[{{list}}],"jti":"p{{number}}"}""")}.psig{number}";
    }

    /// <summary>A token shaped like the API's: three parts, the middle one naming the person. The number keeps each one different.</summary>
    public static string Jwt(Guid userId, int number)
    {
        static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Part("""{"alg":"none"}""")}.{Part($$"""{"sub":"{{userId}}","jti":"{{number}}"}""")}.signature{number}";
    }
}

/// <summary>
/// A stand-in for the API's sign-in, refresh and sign-out. It behaves like the real one where it matters: each refresh token works
/// once, and using an old one again ends the whole session.
/// </summary>
public sealed class FakeAuthApi : HttpMessageHandler
{
    private readonly object _gate = new();
    private int _counter;
    private string? _validRefreshToken;

    public Guid UserId { get; } = Guid.NewGuid();

    public int ExpiresIn { get; set; } = 900;

    /// <summary>When true the API answers a refresh with "invalid refresh token", as if the session was revoked.</summary>
    public bool RefusesRefresh { get; set; }

    /// <summary>When true every call fails as if the API could not be reached.</summary>
    public bool Unreachable { get; set; }

    public TimeSpan RefreshDelay { get; set; }

    public List<string> RefreshTokensSent { get; } = [];

    public List<string> LogoutTokensSent { get; } = [];

    /// <summary>How the fake answers an invitation being accepted; the call is recorded in <see cref="AcceptCalls"/> first.</summary>
    public Func<HttpResponseMessage>? AcceptResponse { get; set; }

    public List<(string? Authorization, string Body)> AcceptCalls { get; } = [];

    /// <summary>The organisations the signed-in person belongs to, as the API's JSON array.</summary>
    public string MembershipsJson { get; set; } = "[]";

    /// <summary>Overrides the answer of <c>GET /api/auth/users/me</c> (an error, for instance).</summary>
    public Func<HttpResponseMessage>? MeResponse { get; set; }

    public List<string?> MeAuthorizations { get; } = [];

    public Func<HttpResponseMessage>? CreateTenantResponse { get; set; }

    public List<(string? Authorization, string Body)> CreateTenantCalls { get; } = [];

    /// <summary>What the tenant-console token allows, as key:Level strings.</summary>
    public string[] ConsolePermissions { get; set; } =
    [
        "members.read:Full", "members.manage:Full", "members.invite:Full", "roles.read:Full", "roles.manage:Full", "roles.assign:Full",
        "access.manage:Full", "apikeys.read:Full", "apikeys.manage:Full", "subscriptions.read:Full", "audit.read:Full",
    ];

    public Func<HttpResponseMessage>? ExchangeResponse { get; set; }

    public List<(string? Authorization, string Body)> ExchangeCalls { get; } = [];

    public List<string> ProductTokensIssued { get; } = [];

    /// <summary>The members of the organisation, as the API's JSON array.</summary>
    public string MembersJson { get; set; } = "[]";

    public Func<HttpResponseMessage>? MembersResponse { get; set; }

    public List<string?> MembersAuthorizations { get; } = [];

    public Func<HttpResponseMessage>? StatusResponse { get; set; }

    public List<(string? Authorization, string Path, string Body)> StatusCalls { get; } = [];

    /// <summary>The pending invitations, as the API's JSON array.</summary>
    public string PendingJson { get; set; } = "[]";

    public Func<HttpResponseMessage>? PendingResponse { get; set; }

    /// <summary>Answers one invitation being sent (given the request body); by default every one is created.</summary>
    public Func<string, HttpResponseMessage>? InviteResponse { get; set; }

    public List<(string? Authorization, string Body)> InviteCalls { get; } = [];

    public Func<HttpResponseMessage>? ResendResponse { get; set; }

    public List<(string? Authorization, string Path)> ResendCalls { get; } = [];

    public string RolesJson { get; set; } = "[]";

    public string SubscriptionsJson { get; set; } = "[]";

    public List<string> AccessTokensIssued { get; } = [];

    public List<string> RefreshTokensIssued { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // A real API answers later, not instantly: pages must cope with the render that happens while they wait.
        await Task.Yield();
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
        using var json = JsonDocument.Parse(body);

        if (Unreachable && path != "/api/auth/login")
        {
            throw new HttpRequestException("the API is down");
        }

        if (path.StartsWith("/api/auth/tenants/me/invitations/", StringComparison.Ordinal) && path.EndsWith("/resend", StringComparison.Ordinal))
        {
            lock (_gate)
            {
                ResendCalls.Add((request.Headers.Authorization?.ToString(), path));
            }

            return ResendResponse?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"membershipId\":\"00000000-0000-0000-0000-000000000000\",\"email\":\"x\",\"status\":\"Invited\",\"roles\":[],\"applicationIds\":[],\"expiresAt\":\"2026-11-09T10:00:00Z\"}", Encoding.UTF8, "application/json"),
            };
        }

        if (path.StartsWith("/api/auth/tenants/me/members/", StringComparison.Ordinal))
        {
            lock (_gate)
            {
                StatusCalls.Add((request.Headers.Authorization?.ToString(), path, body));
            }

            return StatusResponse?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"membershipId\":\"00000000-0000-0000-0000-000000000000\",\"email\":\"x\",\"status\":\"Active\",\"roles\":[],\"applications\":[],\"createdAt\":\"2026-11-01T10:00:00Z\"}", Encoding.UTF8, "application/json"),
            };
        }

        switch (path)
        {
            case "/api/auth/token/exchange":
                lock (_gate)
                {
                    ExchangeCalls.Add((request.Headers.Authorization?.ToString(), body));
                }

                if (ExchangeResponse is not null)
                {
                    return ExchangeResponse();
                }

                string product;
                lock (_gate)
                {
                    product = TestTokens.ProductJwt(UserId, Guid.Parse(json.RootElement.GetProperty("tenantId").GetString()!), ConsolePermissions, ProductTokensIssued.Count + 1);
                    ProductTokensIssued.Add(product);
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""{"accessToken":"{{product}}","tokenType":"Bearer","expiresIn":900}""", Encoding.UTF8, "application/json"),
                };

            case "/api/auth/tenants/me/invitations" when request.Method == HttpMethod.Post:
                lock (_gate)
                {
                    InviteCalls.Add((request.Headers.Authorization?.ToString(), body));
                }

                return InviteResponse?.Invoke(body) ?? new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent("{\"membershipId\":\"00000000-0000-0000-0000-000000000001\",\"email\":\"x\",\"status\":\"Invited\",\"roles\":[],\"applicationIds\":[],\"expiresAt\":\"2026-11-09T10:00:00Z\"}", Encoding.UTF8, "application/json"),
                };

            case "/api/auth/tenants/me/invitations":
                return PendingResponse?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(PendingJson, Encoding.UTF8, "application/json"),
                };

            case "/api/auth/roles":
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(RolesJson, Encoding.UTF8, "application/json") };

            case "/api/auth/tenants/me/subscriptions":
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SubscriptionsJson, Encoding.UTF8, "application/json") };

            case "/api/auth/tenants/me/members":
                lock (_gate)
                {
                    MembersAuthorizations.Add(request.Headers.Authorization?.ToString());
                }

                return MembersResponse?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(MembersJson, Encoding.UTF8, "application/json"),
                };

            case "/api/auth/login":
                return Tokens();

            case "/api/auth/refresh":
                var sent = json.RootElement.GetProperty("refreshToken").GetString()!;
                lock (_gate)
                {
                    RefreshTokensSent.Add(sent);
                }

                if (RefreshDelay > TimeSpan.Zero)
                {
                    await Task.Delay(RefreshDelay, cancellationToken);
                }

                lock (_gate)
                {
                    if (RefusesRefresh || sent != _validRefreshToken)
                    {
                        // A reused token ends the session: nothing works afterwards.
                        _validRefreshToken = null;
                        return Problem(HttpStatusCode.Unauthorized, "invalid_refresh_token");
                    }
                }

                return Tokens();

            case "/api/auth/logout":
                lock (_gate)
                {
                    LogoutTokensSent.Add(json.RootElement.GetProperty("refreshToken").GetString()!);
                }

                return new HttpResponseMessage(HttpStatusCode.NoContent);

            case "/api/auth/users/me":
                lock (_gate)
                {
                    MeAuthorizations.Add(request.Headers.Authorization?.ToString());
                }

                return MeResponse?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $$"""{"id":"{{UserId}}","email":"anna@acme.com","displayName":"Anna Kowalski","emailVerified":true,"createdAt":"2026-11-01T10:00:00Z","memberships":{{MembershipsJson}}}""",
                        Encoding.UTF8,
                        "application/json"),
                };

            case "/api/auth/tenants":
                lock (_gate)
                {
                    CreateTenantCalls.Add((request.Headers.Authorization?.ToString(), body));
                }

                return CreateTenantResponse?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.NotFound);

            case "/api/auth/invitations/accept":
                lock (_gate)
                {
                    AcceptCalls.Add((request.Headers.Authorization?.ToString(), body));
                }

                return AcceptResponse?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.NotFound);

            default:
                return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private HttpResponseMessage Tokens()
    {
        string access;
        string refresh;
        lock (_gate)
        {
            _counter++;
            access = TestTokens.Jwt(UserId, _counter);
            refresh = $"refresh-token-{_counter}";
            _validRefreshToken = refresh;
            AccessTokensIssued.Add(access);
            RefreshTokensIssued.Add(refresh);
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"accessToken":"{{access}}","tokenType":"Bearer","expiresIn":{{ExpiresIn}},"refreshToken":"{{refresh}}","memberships":[]}""",
                Encoding.UTF8,
                "application/json"),
        };
    }

    private static HttpResponseMessage Problem(HttpStatusCode status, string code) => new(status)
    {
        Content = new StringContent(
            $$"""{"status":{{(int)status}},"title":"x","detail":"x","code":"{{code}}"}""", Encoding.UTF8, "application/problem+json"),
    };
}

/// <summary>Records every line the app logs, to check that no password, token or session id is ever in one.</summary>
public sealed class CapturingLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyCollection<string> Lines => [.. _lines];

    public ILogger CreateLogger(string categoryName) => new Capture(categoryName, _lines);

    public void Dispose()
    {
    }

    private sealed class Capture(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue($"{category}: {formatter(state, exception)} {exception}");
    }
}

/// <summary>Fills in and sends the web app's forms the way a browser would: field names come from the page that was rendered.</summary>
public static partial class WebForms
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Loads the sign-in page at <paramref name="path"/>, fills it in and posts it back to the same address.</summary>
    public static async Task<HttpResponseMessage> PostLoginAsync(
        HttpClient client, string email, string password, bool remember = false, string path = "/login")
    {
        var page = await client.GetStringAsync(path, Token);
        var form = new Dictionary<string, string>();
        foreach (Match input in InputTag().Matches(page))
        {
            var tag = input.Value;
            var name = FieldName(tag);
            if (tag.Contains("id=\"login-email\""))
            {
                form[name] = email;
            }
            else if (tag.Contains("id=\"login-password\""))
            {
                form[name] = password;
            }
            else if (tag.Contains("type=\"checkbox\""))
            {
                if (remember)
                {
                    // A checked box sends its value, or "on" when it has none.
                    form[name] = Regex.Match(tag, "value=\"([^\"]*)\"") is { Success: true } value ? value.Groups[1].Value : "on";
                }
            }
            else if (tag.Contains("type=\"hidden\""))
            {
                form[name] = WebUtility.HtmlDecode(Regex.Match(tag, "value=\"([^\"]*)\"").Groups[1].Value);
            }
        }

        return await client.PostAsync(path, new FormUrlEncodedContent(form), Token);
    }

    /// <summary>Posts the sign-out form found on <paramref name="pagePath"/>, or posts without its anti-forgery token.</summary>
    public static async Task<HttpResponseMessage> PostLogoutAsync(HttpClient client, string pagePath = "/account", bool withToken = true)
    {
        var form = new Dictionary<string, string>();
        if (withToken)
        {
            var page = await client.GetStringAsync(pagePath, Token);
            var logout = Regex.Match(page, """<form[^>]*action="/logout"[^>]*>(?<body>.*?)</form>""", RegexOptions.Singleline);
            logout.Success.ShouldBeTrue("the page has a sign-out form");
            foreach (Match input in InputTag().Matches(logout.Groups["body"].Value))
            {
                if (input.Value.Contains("type=\"hidden\""))
                {
                    form[FieldName(input.Value)] = WebUtility.HtmlDecode(Regex.Match(input.Value, "value=\"([^\"]*)\"").Groups[1].Value);
                }
            }
        }

        return await client.PostAsync("/logout", new FormUrlEncodedContent(form), Token);
    }

    /// <summary>Loads <paramref name="path"/>, fills in the form named <paramref name="formName"/> (text values by input id) and posts it.</summary>
    public static async Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string path, string formName, IReadOnlyDictionary<string, string> valuesById)
    {
        var page = await client.GetStringAsync(path, Token);
        return await PostFormFromHtmlAsync(client, path, page, formName, valuesById);
    }

    /// <summary>Posts the form named <paramref name="formName"/> found in <paramref name="html"/>, as a browser would.</summary>
    public static async Task<HttpResponseMessage> PostFormFromHtmlAsync(
        HttpClient client, string path, string html, string formName, IReadOnlyDictionary<string, string>? valuesById = null)
    {
        foreach (Match form in Regex.Matches(html, "<form(?<attributes>[^>]*)>(?<body>.*?)</form>", RegexOptions.Singleline))
        {
            // A browser posts to the form's action when it has one, otherwise to the address it is on.
            var action = Regex.Match(form.Groups["attributes"].Value, "\\baction=\"([^\"]*)\"") is { Success: true } found
                ? WebUtility.HtmlDecode(found.Groups[1].Value)
                : path;
            var inputs = InputTag().Matches(form.Groups["body"].Value).Select(match => match.Value).ToList();
            var handler = inputs.FirstOrDefault(tag => FieldName(tag) == "_handler");
            if (handler is null || Regex.Match(handler, "value=\"([^\"]*)\"").Groups[1].Value != formName)
            {
                continue;
            }

            var fields = new Dictionary<string, string>();
            foreach (var tag in inputs)
            {
                var id = Regex.Match(tag, "\\bid=\"([^\"]+)\"").Groups[1].Value;
                if (id.Length > 0 && valuesById is not null && valuesById.TryGetValue(id, out var typed))
                {
                    fields[FieldName(tag)] = typed;
                }
                else if (tag.Contains("type=\"hidden\""))
                {
                    fields[FieldName(tag)] = WebUtility.HtmlDecode(Regex.Match(tag, "value=\"([^\"]*)\"").Groups[1].Value);
                }
                else if (!tag.Contains("type=\"checkbox\""))
                {
                    // A text box nobody typed in still sends its (empty) value.
                    fields[FieldName(tag)] = "";
                }
            }

            return await client.PostAsync(action, new FormUrlEncodedContent(fields), Token);
        }

        throw new InvalidOperationException($"The page has no form named \"{formName}\".");
    }

    /// <summary>Posts the plain form whose action contains <paramref name="action"/>, with every hidden field it carries (its anti-forgery token, the status).</summary>
    public static async Task<HttpResponseMessage> PostPlainFormAsync(HttpClient client, string html, string action)
    {
        foreach (Match form in Regex.Matches(html, "<form(?<attributes>[^>]*)>(?<body>.*?)</form>", RegexOptions.Singleline))
        {
            var target = Regex.Match(form.Groups["attributes"].Value, "\\baction=\"([^\"]*)\"") is { Success: true } found ? WebUtility.HtmlDecode(found.Groups[1].Value) : "";
            if (!target.Contains(action, StringComparison.Ordinal))
            {
                continue;
            }

            var fields = new Dictionary<string, string>();
            foreach (Match input in InputTag().Matches(form.Groups["body"].Value))
            {
                if (input.Value.Contains("type=\"hidden\""))
                {
                    fields[FieldName(input.Value)] = WebUtility.HtmlDecode(Regex.Match(input.Value, "value=\"([^\"]*)\"").Groups[1].Value);
                }
            }

            return await client.PostAsync(target, new FormUrlEncodedContent(fields), Token);
        }

        throw new InvalidOperationException($"The page has no form posting to \"{action}\".");
    }

    /// <summary>Posts the sign-out form found in <paramref name="html"/> with every hidden field it carries (its anti-forgery token, any return page).</summary>
    public static async Task<HttpResponseMessage> PostLogoutFromHtmlAsync(HttpClient client, string html)
    {
        var logout = Regex.Match(html, "<form[^>]*action=\"/logout\"[^>]*>(?<body>.*?)</form>", RegexOptions.Singleline);
        logout.Success.ShouldBeTrue("the page has a sign-out form");
        var form = new Dictionary<string, string>();
        foreach (Match input in InputTag().Matches(logout.Groups["body"].Value))
        {
            if (input.Value.Contains("type=\"hidden\""))
            {
                form[FieldName(input.Value)] = WebUtility.HtmlDecode(Regex.Match(input.Value, "value=\"([^\"]*)\"").Groups[1].Value);
            }
        }

        return await client.PostAsync("/logout", new FormUrlEncodedContent(form), Token);
    }

    private static string FieldName(string tag) => WebUtility.HtmlDecode(Regex.Match(tag, "name=\"([^\"]+)\"").Groups[1].Value);

    [GeneratedRegex("<input[^>]*>")]
    private static partial Regex InputTag();
}
