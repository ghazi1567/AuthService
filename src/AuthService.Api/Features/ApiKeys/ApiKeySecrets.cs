using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace AuthService.Api.Features.ApiKeys;

/// <summary>
/// The shape of an API key and how it is checked. A raw key is <c>ak_{keyId}.{secret}</c>: the key id is 24 hex characters and is
/// not secret (it finds the row and is shown masked in lists); the secret is 256 random bits. Only a salted SHA-256 of the secret is
/// stored. The secret is long and random, so a fast hash is enough and a leaked database cannot be reversed into a working key.
/// </summary>
public static class ApiKeySecrets
{
    private const string Prefix = "ak_";
    private const int KeyIdHexLength = 24;

    public sealed record Generated(string RawKey, string KeyId, string Salt, string Hash);

    public static Generated Generate()
    {
        var keyId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(KeyIdHexLength / 2));
        var secret = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var salt = RandomNumberGenerator.GetBytes(16);

        return new Generated($"{Prefix}{keyId}.{secret}", keyId, Convert.ToBase64String(salt), Hash(salt, secret));
    }

    /// <summary>Splits a presented key into its public id and secret, or false when it is not shaped like a key at all.</summary>
    public static bool TryParse(string? rawKey, out string keyId, out string secret)
    {
        keyId = secret = string.Empty;
        if (rawKey is null || !rawKey.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var dot = rawKey.IndexOf('.', StringComparison.Ordinal);
        if (dot != Prefix.Length + KeyIdHexLength || dot == rawKey.Length - 1)
        {
            return false;
        }

        keyId = rawKey[Prefix.Length..dot];
        secret = rawKey[(dot + 1)..];
        return keyId.All(character => char.IsAsciiHexDigitLower(character)) && secret.Length is > 0 and <= 100;
    }

    /// <summary>The masked form shown after creation: enough to recognise a key, never enough to use it.</summary>
    public static string Mask(string keyId) => $"{Prefix}{keyId[..8]}...";

    public static string Hash(ReadOnlySpan<byte> salt, string secret)
    {
        var secretBytes = System.Text.Encoding.UTF8.GetBytes(secret);
        return Convert.ToBase64String(SHA256.HashData([.. salt, .. secretBytes]));
    }

    /// <summary>Compares in constant time, so how long the answer takes says nothing about how much of the secret was right.</summary>
    public static bool Verify(string saltBase64, string expectedHashBase64, string secret)
    {
        var actual = Hash(Convert.FromBase64String(saltBase64), secret);
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(actual), System.Text.Encoding.ASCII.GetBytes(expectedHashBase64));
    }
}
