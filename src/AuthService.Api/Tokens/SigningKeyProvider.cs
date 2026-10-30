using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AuthService.Api.Tokens;

/// <summary>
/// Holds the key that signs tokens. Today it is one key from configuration; TOK-02 replaces this with rotating keys
/// published through JWKS. Created at startup so a missing key fails the deployment, not the first login.
/// </summary>
public sealed class SigningKeyProvider : IDisposable
{
    private readonly ECDsa _ecdsa;

    public SigningKeyProvider(IOptions<IdentityTokenOptions> options, IHostEnvironment environment)
    {
        _ecdsa = ECDsa.Create();

        var pem = options.Value.SigningKeyPem;
        if (!string.IsNullOrWhiteSpace(pem))
        {
            _ecdsa.ImportFromPem(pem);
        }
        else if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            // Throwaway key: tokens stop validating when the process restarts. Fine for development and tests only.
            _ecdsa.GenerateKey(ECCurve.NamedCurves.nistP256);
        }
        else
        {
            _ecdsa.Dispose();
            throw new InvalidOperationException(
                $"{IdentityTokenOptions.SectionName}:{nameof(IdentityTokenOptions.SigningKeyPem)} is not configured.");
        }

        var key = new ECDsaSecurityKey(_ecdsa);
        key.KeyId = Base64UrlEncoder.Encode(JsonWebKeyConverter.ConvertFromECDsaSecurityKey(key).ComputeJwkThumbprint());
        Key = key;
    }

    public ECDsaSecurityKey Key { get; }

    public void Dispose() => _ecdsa.Dispose();
}
