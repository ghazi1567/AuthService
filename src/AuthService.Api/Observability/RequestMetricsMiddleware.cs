using System.Diagnostics;

namespace AuthService.Api.Observability;

/// <summary>Counts every request and how long it took, by route template and status code (PLT-05).</summary>
public sealed class RequestMetricsMiddleware(RequestDelegate next, AuthMetrics metrics)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        var failed = false;

        try
        {
            await next(context);
        }
        catch
        {
            // Normally the exception handler has turned an error into a 500 before it gets here; this is for one that slips past it.
            failed = true;
            throw;
        }
        finally
        {
            // The template ("/api/auth/tenants/{tenantId:guid}"), never the path, so ids and tokens never become labels.
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
            var status = failed ? StatusCodes.Status500InternalServerError : context.Response.StatusCode;
            metrics.RecordRequest(context.Request.Method, route, status, Stopwatch.GetElapsedTime(started));
        }
    }
}
