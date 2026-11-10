using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AuthService.Entitlements;

/// <summary>
/// Checks that a webhook came from the Auth Service and is recent. The signature is the HMAC-SHA256, in lowercase hex, of
/// <c>{timestamp}.{body}</c> with the product's signing secret, sent as <c>X-Auth-Signature: v1=&lt;hex&gt;</c>; during a secret rotation
/// the header carries one <c>v1=</c> entry per secret, comma-separated, and any one matching is enough. The timestamp (Unix seconds,
/// <c>X-Auth-Timestamp</c>) must be within <see cref="Tolerance"/> of now, so a captured request cannot be replayed later.
/// </summary>
public sealed class AuthWebhookVerifier
{
    /// <summary>The header with the event's id.</summary>
    public const string EventIdHeader = "X-Auth-Event-Id";

    /// <summary>The header with the event's type.</summary>
    public const string EventTypeHeader = "X-Auth-Event-Type";

    /// <summary>The header with the Unix time the event was signed.</summary>
    public const string TimestampHeader = "X-Auth-Timestamp";

    /// <summary>The header with the signature or signatures.</summary>
    public const string SignatureHeader = "X-Auth-Signature";

    private const string Scheme = "v1=";

    private readonly IReadOnlyList<byte[]> _secrets;
    private readonly TimeProvider _time;

    /// <summary>Creates a verifier for the product's signing secrets: the current one, and during a rotation the previous one too.</summary>
    public AuthWebhookVerifier(IEnumerable<string> secrets, TimeProvider? time = null, TimeSpan? tolerance = null)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        _secrets = [.. secrets.Where(secret => !string.IsNullOrWhiteSpace(secret)).Select(secret => Encoding.UTF8.GetBytes(secret.Trim()))];
        if (_secrets.Count == 0)
        {
            throw new ArgumentException("At least one webhook signing secret is needed: the one shown when the product's callback was registered.", nameof(secrets));
        }

        _time = time ?? TimeProvider.System;
        Tolerance = tolerance ?? TimeSpan.FromMinutes(5);
    }

    /// <summary>How far the signed time may be from now, either way. Five minutes unless set.</summary>
    public TimeSpan Tolerance { get; }

    /// <summary>Whether <paramref name="body"/>, exactly as received, was signed by the Auth Service with one of the secrets, recently.</summary>
    public bool Verify(string? timestamp, string? signatureHeader, string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || string.IsNullOrWhiteSpace(signatureHeader))
        {
            return false;
        }

        var signedAt = DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(seconds, 0, 253402300799));
        if ((_time.GetUtcNow() - signedAt).Duration() > Tolerance)
        {
            return false;
        }

        var payload = Encoding.UTF8.GetBytes($"{seconds}.{body}");
        var expected = _secrets.Select(secret => HMACSHA256.HashData(secret, payload)).ToList();
        foreach (var entry in signatureHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!entry.StartsWith(Scheme, StringComparison.Ordinal) || !TryHex(entry[Scheme.Length..], out var given))
            {
                continue;
            }

            if (expected.Any(mac => CryptographicOperations.FixedTimeEquals(mac, given)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The <c>X-Auth-Signature</c> value the Auth Service would send for this body: for tests and fakes.</summary>
    public static string Sign(string secret, long timestamp, string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentNullException.ThrowIfNull(body);
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret.Trim()), Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
        return Scheme + Convert.ToHexString(mac).ToLowerInvariant();
    }

    private static bool TryHex(string text, out byte[] bytes)
    {
        bytes = [];
        if (text.Length != 64)
        {
            return false;
        }

        try
        {
            bytes = Convert.FromHexString(text);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
