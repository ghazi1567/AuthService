using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AuthService.Web.Api;

/// <summary>
/// The web app's one way of calling the Auth Service API. Every call turns an API error into an <see cref="ApiProblem"/> instead of
/// throwing, so pages decide what to show. It never follows redirects (the API does not redirect) and holds no token itself: callers
/// pass the person's token for a call, and the session keeps it on the server.
/// </summary>
public sealed class AuthApiClient(HttpClient httpClient)
{
    /// <summary>Asks the API's root endpoint, which answers with its name when it is running.</summary>
    public Task<ApiResult<string>> PingAsync(CancellationToken cancellationToken) =>
        SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/"),
            (response, token) => response.Content.ReadAsStringAsync(token),
            cancellationToken);

    /// <summary>Reads a JSON resource, optionally as the person holding <paramref name="bearerToken"/>.</summary>
    public Task<ApiResult<T>> GetAsync<T>(string path, string? bearerToken, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (bearerToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        return SendAsync(
            request,
            async (response, token) => await response.Content.ReadFromJsonAsync<T>(token)
                ?? throw new InvalidOperationException("The API returned an empty body."),
            cancellationToken);
    }

    private async Task<ApiResult<T>> SendAsync<T>(
        HttpRequestMessage request,
        Func<HttpResponseMessage, CancellationToken, Task<T>> read,
        CancellationToken cancellationToken)
    {
        using (request)
        {
            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException)
            {
                return ApiResult<T>.Failure(ApiProblem.FromUnreachable());
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The client's own timeout, not the caller giving up.
                return ApiResult<T>.Failure(ApiProblem.FromUnreachable());
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    try
                    {
                        return ApiResult<T>.Success(await read(response, cancellationToken));
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or JsonException or NotSupportedException)
                    {
                        return ApiResult<T>.Failure(new ApiProblem((int)response.StatusCode, ApiProblem.Unexpected));
                    }
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var retryAfter = response.Headers.RetryAfter?.Delta;
                return ApiResult<T>.Failure(ApiProblem.Parse((int)response.StatusCode, body, retryAfter));
            }
        }
    }
}
