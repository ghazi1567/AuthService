using System.Security.Cryptography;
using System.Text;

namespace AuthService.Api.Features.Webhooks;

/// <summary>
/// Signs a webhook so a product can tell it came from the Auth Service and was not replayed. The signature is the HMAC-SHA256, in
/// lowercase hex, of <c>{timestamp}.{body}</c> with the product's signing secret, sent as <c>X-Auth-Signature: v1=&lt;hex&gt;</c>.
/// The timestamp (Unix seconds) is sent as <c>X-Auth-Timestamp</c> and is inside what is signed, so a product can reject an old one.
/// </summary>
public static class WebhookSigner
{
    public const string Version = "v1";

    public static string NewSecret() => "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static string Sign(string secret, long timestamp, string body)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
        return $"{Version}={Convert.ToHexStringLower(mac)}";
    }

    /// <summary>
    /// The header value for one or more secrets, newest first, as comma-separated <c>v1=&lt;hex&gt;</c> entries. During the overlap after a
    /// rotation (SUB-07) that is two entries; a receiver accepts the event if any one of them verifies. Outside it, one entry, exactly
    /// as before.
    /// </summary>
    public static string SignAll(IEnumerable<string> secrets, long timestamp, string body) =>
        string.Join(',', secrets.Select(secret => Sign(secret, timestamp, body)));
}
