using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace AuthService.Api.Tokens;

/// <summary>
/// Encrypts signing keys before they are stored (AES-256-GCM), so a leaked database does not let anyone forge tokens. The
/// stored form is base64 of nonce, tag and ciphertext, and decrypting with the wrong secret fails instead of returning junk.
/// </summary>
public sealed class KeyEncryption
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public KeyEncryption(IOptions<IdentityTokenOptions> options, IHostEnvironment environment)
    {
        var configured = options.Value.KeyEncryptionKey;

        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                _key = Convert.FromBase64String(configured);
            }
            catch (FormatException)
            {
                throw new InvalidOperationException($"{IdentityTokenOptions.SectionName}:{nameof(IdentityTokenOptions.KeyEncryptionKey)} is not valid base64.");
            }

            if (_key.Length != 32)
            {
                throw new InvalidOperationException(
                    $"{IdentityTokenOptions.SectionName}:{nameof(IdentityTokenOptions.KeyEncryptionKey)} must be 32 bytes (base64 of 32 random bytes).");
            }
        }
        else if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            // A fixed, publicly known key: fine for a development database and tests, worthless for anything real. It keeps
            // keys stored by one run readable by the next, so a development database survives restarts.
            _key = SHA256.HashData(Encoding.UTF8.GetBytes("AuthService development and test key. Never used in production."));
        }
        else
        {
            throw new InvalidOperationException(
                $"{IdentityTokenOptions.SectionName}:{nameof(IdentityTokenOptions.KeyEncryptionKey)} is not configured.");
        }
    }

    public string Encrypt(byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        return Convert.ToBase64String([.. nonce, .. tag, .. ciphertext]);
    }

    public byte[] Decrypt(string protectedValue)
    {
        var all = Convert.FromBase64String(protectedValue);
        var nonce = all.AsSpan(0, NonceSize);
        var tag = all.AsSpan(NonceSize, TagSize);
        var ciphertext = all.AsSpan(NonceSize + TagSize);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return plaintext;
    }
}
