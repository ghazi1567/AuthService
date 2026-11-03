namespace AuthService.Web.Api;

/// <summary>
/// Tells the API who the visitor really is. The web app calls the API for its visitors, so without this every request would arrive from the
/// web app's own address: the API's per-address rate limits would be one shared budget for everyone, and its audit entries would all name
/// the web app. The address sent is the one the web app itself worked out for the request (after believing only its trusted proxies), never
/// a header copied from the visitor, so a visitor cannot choose it. The API believes <c>X-Forwarded-For</c> only from the web app's address
/// (<c>Auth:Proxy</c> there).
/// </summary>
public sealed class ClientAddressHandler(IHttpContextAccessor httpContextAccessor) : DelegatingHandler
{
    public const string Header = "X-Forwarded-For";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Whatever the request carried is replaced, so it can only ever be the address the web app saw.
        request.Headers.Remove(Header);

        if (httpContextAccessor.HttpContext?.Connection.RemoteIpAddress is { } address)
        {
            request.Headers.TryAddWithoutValidation(Header, (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString());
        }

        return base.SendAsync(request, cancellationToken);
    }
}
