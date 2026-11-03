using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace AuthService.Api.Observability;

/// <summary>
/// The service's own counters, kept in memory and written out in the Prometheus text format by <c>/metrics</c> (PLT-05). They count
/// since the process started; the monitoring system turns that into rates. Labels are only ever fixed words (a route template, a
/// status code, a failure reason), never an email, a name or an id, so the metrics hold no personal data.
/// </summary>
public sealed class AuthMetrics
{
    private static readonly double[] DurationBuckets = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10];

    private static readonly HashSet<string> KnownMethods = new(StringComparer.Ordinal)
    {
        "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS",
    };

    private readonly ConcurrentDictionary<(string Method, string Route, int Status), long> _requests = new();
    private readonly ConcurrentDictionary<(string Method, string Route), Histogram> _durations = new();
    private readonly ConcurrentDictionary<string, long> _loginFailures = new();

    /// <summary>Counts one finished request. A route that matched nothing is counted as <c>unmatched</c>, so scanners cannot create labels.</summary>
    public void RecordRequest(string method, string? routeTemplate, int statusCode, TimeSpan duration)
    {
        method = KnownMethods.Contains(method) ? method : "OTHER";
        var route = string.IsNullOrEmpty(routeTemplate) ? "unmatched" : routeTemplate;

        _requests.AddOrUpdate((method, route, statusCode), 1, static (_, count) => count + 1);
        _durations.GetOrAdd((method, route), static _ => new Histogram()).Observe(duration.TotalSeconds);
    }

    /// <summary>Counts one refused login. <paramref name="reason"/> is a fixed word such as <c>wrong_password</c>.</summary>
    public void RecordLoginFailure(string reason) =>
        _loginFailures.AddOrUpdate(reason, 1, static (_, count) => count + 1);

    /// <summary>Writes the counters in the Prometheus text format.</summary>
    public void WriteTo(StringBuilder text)
    {
        text.AppendLine("# HELP auth_http_requests_total Requests answered, by method, route template and status code.");
        text.AppendLine("# TYPE auth_http_requests_total counter");
        foreach (var ((method, route, status), count) in _requests
                     .OrderBy(pair => pair.Key.Route, StringComparer.Ordinal)
                     .ThenBy(pair => pair.Key.Method, StringComparer.Ordinal)
                     .ThenBy(pair => pair.Key.Status))
        {
            text.Append("auth_http_requests_total{method=\"").Append(method).Append("\",route=\"").Append(Escape(route))
                .Append("\",status=\"").Append(status.ToString(CultureInfo.InvariantCulture)).Append("\"} ")
                .AppendLine(count.ToString(CultureInfo.InvariantCulture));
        }

        text.AppendLine("# HELP auth_http_request_duration_seconds How long requests took to answer, by method and route template.");
        text.AppendLine("# TYPE auth_http_request_duration_seconds histogram");
        foreach (var ((method, route), histogram) in _durations
                     .OrderBy(pair => pair.Key.Route, StringComparer.Ordinal)
                     .ThenBy(pair => pair.Key.Method, StringComparer.Ordinal))
        {
            var labels = $"method=\"{method}\",route=\"{Escape(route)}\"";
            var (counts, sum, total) = histogram.Snapshot();

            var cumulative = 0L;
            for (var i = 0; i < DurationBuckets.Length; i++)
            {
                cumulative += counts[i];
                text.Append("auth_http_request_duration_seconds_bucket{").Append(labels).Append(",le=\"")
                    .Append(DurationBuckets[i].ToString(CultureInfo.InvariantCulture)).Append("\"} ")
                    .AppendLine(cumulative.ToString(CultureInfo.InvariantCulture));
            }

            text.Append("auth_http_request_duration_seconds_bucket{").Append(labels).Append(",le=\"+Inf\"} ")
                .AppendLine(total.ToString(CultureInfo.InvariantCulture));
            text.Append("auth_http_request_duration_seconds_sum{").Append(labels).Append("} ")
                .AppendLine(sum.ToString("R", CultureInfo.InvariantCulture));
            text.Append("auth_http_request_duration_seconds_count{").Append(labels).Append("} ")
                .AppendLine(total.ToString(CultureInfo.InvariantCulture));
        }

        text.AppendLine("# HELP auth_login_failures_total Refused logins, by reason.");
        text.AppendLine("# TYPE auth_login_failures_total counter");
        foreach (var (reason, count) in _loginFailures.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            text.Append("auth_login_failures_total{reason=\"").Append(Escape(reason)).Append("\"} ")
                .AppendLine(count.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>A label value may not contain a quote, a backslash or a line break unescaped.</summary>
    internal static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    private sealed class Histogram
    {
        private readonly long[] _counts = new long[DurationBuckets.Length];
        private readonly object _lock = new();
        private double _sum;
        private long _total;

        public void Observe(double seconds)
        {
            lock (_lock)
            {
                _sum += seconds;
                _total++;

                // Each bucket holds the observations up to its limit and above the one before it; the writer adds them up.
                for (var i = 0; i < DurationBuckets.Length; i++)
                {
                    if (seconds <= DurationBuckets[i])
                    {
                        _counts[i]++;
                        break;
                    }
                }
            }
        }

        public (long[] Counts, double Sum, long Total) Snapshot()
        {
            lock (_lock)
            {
                return ([.. _counts], _sum, _total);
            }
        }
    }
}
