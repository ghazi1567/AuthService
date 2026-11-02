using System.Security.Claims;
using AuthService.Web.Api;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace AuthService.Web.Security;

/// <summary>
/// Cookie sign-in for the web app. The cookie holds an encrypted session id and the person's name, never a token: the tokens stay in
/// the <see cref="ISessionStore"/> on the server. Every request checks the session, which renews its tokens when they are about to
/// run out; a session that has ended sends the person back to sign in, with the page they wanted remembered.
/// </summary>
public static class SessionAuthentication
{
    public const string Scheme = "AuthService.Session";

    /// <summary>The claim on the cookie that names the server-side session.</summary>
    public const string SessionIdClaim = "sid";

    public const string LoginPath = "/login";
    public const string LogoutPath = "/logout";

    public static IServiceCollection AddSessionAuthentication(this IServiceCollection services, IHostEnvironment environment)
    {
        services.AddOptions<SessionOptions>()
            .BindConfiguration(SessionOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISessionStore, MemorySessionStore>();
        services.AddScoped<SessionService>();
        services.AddSingleton<PendingInvitation>();
        services.AddSingleton<ConsoleTokenCache>();
        services.AddSingleton<OneTimeSecrets>();
        services.AddScoped<ConsoleSession>();
        services.AddScoped<PlatformSession>();

        services.AddAuthentication(Scheme).AddCookie(Scheme, options =>
        {
            options.LoginPath = LoginPath;
            options.ReturnUrlParameter = "returnUrl";
            options.Cookie.HttpOnly = true;

            // Lax, not Strict, so following a link from an email or another product still arrives signed in.
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;

            // The session decides how long a person stays signed in, not a cookie that is stretched on each visit.
            options.SlidingExpiration = false;

            options.Events = new CookieAuthenticationEvents
            {
                OnValidatePrincipal = async context =>
                {
                    var sessionId = context.Principal?.FindFirstValue(SessionIdClaim);
                    var sessions = context.HttpContext.RequestServices.GetRequiredService<SessionService>();
                    if (sessionId is null || await sessions.EnsureFreshAsync(sessionId, context.HttpContext.RequestAborted) == SessionState.Ended)
                    {
                        context.RejectPrincipal();
                        await context.HttpContext.SignOutAsync(Scheme);
                    }
                },
                OnRedirectToLogin = context =>
                {
                    // A browser that still sent a session cookie had a session that has now ended, which the login page says.
                    var cookieName = context.HttpContext.RequestServices.GetRequiredService<IOptions<SessionOptions>>().Value.CookieName;
                    var target = context.Request.Cookies.ContainsKey(cookieName)
                        ? QueryHelpers.AddQueryString(context.RedirectUri, "reason", "expired")
                        : context.RedirectUri;
                    context.Response.Redirect(target);
                    return Task.CompletedTask;
                },
            };
        });

        services.AddOptions<CookieAuthenticationOptions>(Scheme)
            .Configure<IOptions<SessionOptions>, TimeProvider>((cookie, session, time) =>
            {
                cookie.Cookie.Name = session.Value.CookieName;
                cookie.TimeProvider = time;
            });

        services.AddAuthorization();
        services.AddCascadingAuthenticationState();
        return services;
    }

    /// <summary>Signs a person in with the cookie for a session that was just started.</summary>
    public static Task SignInAsync(this HttpContext httpContext, UserSession session, bool remember)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, session.UserId.ToString()),
                new Claim(ClaimTypes.Name, session.Email),
                new Claim(SessionIdClaim, session.Id),
            ],
            Scheme);

        // "Keep me signed in" makes a cookie that outlives the browser; otherwise it ends when the browser closes.
        return httpContext.SignInAsync(
            Scheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = remember, ExpiresUtc = session.ExpiresAt });
    }

    /// <summary>Maps <c>POST /logout</c>: ends the session here and at the API, clears the cookie and goes to the login page.</summary>
    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(LogoutPath, async (HttpContext context, IAntiforgery antiforgery, SessionService sessions) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest();
            }

            if (context.User.FindFirstValue(SessionIdClaim) is { } sessionId)
            {
                await sessions.EndAsync(sessionId, context.RequestAborted);
            }

            await context.SignOutAsync(Scheme);

            // Switching account: the person signs in again and lands back on the page they came from (a path on this site only).
            var form = await context.Request.ReadFormAsync();
            var returnUrl = LocalRedirect.Safe(form["returnUrl"]);
            return Results.Redirect(returnUrl == LocalRedirect.Home ? LoginPath : $"{LoginPath}?returnUrl={Uri.EscapeDataString(returnUrl)}");
        }).AllowAnonymous();

        return app;
    }
}
