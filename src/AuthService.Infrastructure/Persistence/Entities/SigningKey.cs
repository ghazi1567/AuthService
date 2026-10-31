namespace AuthService.Infrastructure.Persistence.Entities;

/// <summary>
/// A key the service signs tokens with (TOK-02). Keys live in the database so every instance agrees on them, and the
/// private half is stored encrypted, never in plain text.
///
/// A key goes through four moments: it is published in the JWKS (<see cref="PublishFrom"/>), later it starts signing
/// (<see cref="SignFrom"/>), later it stops signing (<see cref="SignUntil"/>), and finally it is removed from the JWKS
/// (<see cref="PublishUntil"/>). Publishing ahead of signing gives every product time to learn the key before a token
/// carries it; keeping it published after signing stops lets tokens already issued keep validating.
/// </summary>
public sealed class SigningKey
{
    public Guid Id { get; set; }

    /// <summary>The <c>kid</c> in token headers and in the JWKS: the key's RFC 7638 thumbprint.</summary>
    public required string KeyId { get; set; }

    /// <summary>Public EC point, as base64url (the JWK <c>x</c> and <c>y</c>).</summary>
    public required string PublicKeyX { get; set; }

    public required string PublicKeyY { get; set; }

    /// <summary>The PKCS#8 private key, encrypted with AES-256-GCM under the key-encryption key, as base64.</summary>
    public required string EncryptedPrivateKey { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset PublishFrom { get; set; }

    public DateTimeOffset SignFrom { get; set; }

    /// <summary>Null while this is the newest key, with no successor scheduled yet.</summary>
    public DateTimeOffset? SignUntil { get; set; }

    /// <summary>Null while the key is still published.</summary>
    public DateTimeOffset? PublishUntil { get; set; }
}
