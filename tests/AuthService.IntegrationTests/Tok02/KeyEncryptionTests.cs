using System.Security.Cryptography;
using AuthService.Api.Tokens;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AuthService.IntegrationTests.Tok02;

/// <summary>
/// TOK-02 (AUTH-27): how signing keys are protected at rest. Pure unit tests: no database, no service.
/// These tests have no workbook Test Case ID yet: the spec's acceptance criteria cover them.
/// </summary>
public sealed class KeyEncryptionTests
{
    private static readonly string ValidKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static KeyEncryption Create(string? key, string environment) =>
        new(
            Options.Create(new IdentityTokenOptions { KeyEncryptionKey = key }),
            new StubEnvironment(environment));

    [Fact]
    public void TOK02_AValueEncryptedWithAKeyDecryptsBackToTheOriginal()
    {
        var encryption = Create(ValidKey, Environments.Production);
        var secret = RandomNumberGenerator.GetBytes(100);

        encryption.Decrypt(encryption.Encrypt(secret)).ShouldBe(secret);
    }

    [Fact]
    public void TOK02_TheStoredFormNeverContainsThePlaintextAndDiffersEveryTime()
    {
        var encryption = Create(ValidKey, Environments.Production);
        var secret = RandomNumberGenerator.GetBytes(100);

        var first = encryption.Encrypt(secret);
        var second = encryption.Encrypt(secret);

        first.ShouldNotBe(second, "each encryption uses a fresh random nonce");
        Convert.FromBase64String(first).AsSpan().IndexOf(secret).ShouldBe(-1);
    }

    [Fact]
    public void TOK02_AnotherKeyCannotDecryptIt()
    {
        var stored = Create(ValidKey, Environments.Production).Encrypt(RandomNumberGenerator.GetBytes(100));
        var otherKey = Create(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), Environments.Production);

        Should.Throw<CryptographicException>(() => otherKey.Decrypt(stored));
    }

    [Fact]
    public void TOK02_ATamperedValueIsRejectedInsteadOfDecryptingToJunk()
    {
        var encryption = Create(ValidKey, Environments.Production);
        var bytes = Convert.FromBase64String(encryption.Encrypt(RandomNumberGenerator.GetBytes(100)));
        bytes[^1] ^= 0xFF;

        Should.Throw<CryptographicException>(() => encryption.Decrypt(Convert.ToBase64String(bytes)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TOK02_WithoutAKeyProductionRefusesToStart(string? key)
    {
        var failure = Should.Throw<InvalidOperationException>(() => Create(key, Environments.Production));

        failure.Message.ShouldContain("KeyEncryptionKey");
    }

    [Theory]
    [InlineData("not base64 at all!")]
    [InlineData("c2hvcnQ=")]
    public void TOK02_AKeyThatIsNotBase64Of32BytesIsRefused(string key)
    {
        Should.Throw<InvalidOperationException>(() => Create(key, Environments.Production));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void TOK02_DevelopmentAndTestingFallBackToAFixedKeySoADatabaseSurvivesRestarts(string environment)
    {
        var stored = Create(null, environment).Encrypt([1, 2, 3]);

        Create(null, environment).Decrypt(stored).ShouldBe([1, 2, 3]);
    }

    private sealed class StubEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Tests";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
