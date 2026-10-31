using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace AuthService.Api.Common;

/// <summary>
/// Single-use secrets such as refresh and verification tokens: random, handed to the person once, and stored only as a
/// hash so a database leak cannot be replayed.
/// </summary>
public static class SecretToken
{
    /// <summary>A new random token, 256 bits, safe to put in a link.</summary>
    public static string NewValue() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    /// <summary>The form in which a token is stored and looked up: SHA-256 as 64 uppercase hex characters.</summary>
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
