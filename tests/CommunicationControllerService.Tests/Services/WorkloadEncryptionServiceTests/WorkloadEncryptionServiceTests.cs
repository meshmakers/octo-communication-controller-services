using System.Security.Cryptography;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Sdk.Common.Encryption;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.WorkloadEncryptionServiceTests;

internal class WorkloadEncryptionServiceTests
{
    private const string SentinelV1 = "enc:v1:";

    private static readonly IInstanceSecretCrypto Crypto = new InstanceSecretCrypto();

    private static string ValidKeyBase64()
    {
        var key = new byte[WorkloadEncryptionService.KeyLength];
        // Deterministic key so test failures are reproducible — never reuse in prod.
        for (var i = 0; i < key.Length; i++) key[i] = (byte)(i + 1);
        return Convert.ToBase64String(key);
    }

    private static WorkloadEncryptionService CreateSut(string? keyBase64 = null)
    {
        var options = Substitute.For<IOptions<CommunicationControllerOptions>>();
        options.Value.Returns(new CommunicationControllerOptions
        {
            InstanceSecretKey = keyBase64 ?? ValidKeyBase64()
        });
        return new WorkloadEncryptionService(options, Crypto);
    }

    [Test]
    public async Task Encrypt_ThenDecrypt_Roundtrips()
    {
        var sut = CreateSut();

        var cipher = sut.Encrypt("hello-world");

        await Assert.That(cipher).StartsWith(SentinelV1);
        await Assert.That(sut.Decrypt(cipher)).IsEqualTo("hello-world");
    }

    [Test]
    public async Task Encrypt_TwoCallsForSameInput_ProduceDifferentCiphertexts()
    {
        // Sanity check: GCM nonce is randomly generated each call.
        var sut = CreateSut();

        var a = sut.Encrypt("same-plaintext");
        var b = sut.Encrypt("same-plaintext");

        await Assert.That(a).IsNotEqualTo(b);
        await Assert.That(sut.Decrypt(a)).IsEqualTo("same-plaintext");
        await Assert.That(sut.Decrypt(b)).IsEqualTo("same-plaintext");
    }

    [Test]
    public async Task Decrypt_PlaintextWithoutSentinel_ReturnsAsIs()
    {
        // The sentinel-skip lets a single field carry either plain or encrypted
        // data — important for the migration path and for local dev where
        // secrets are not yet filled in.
        var sut = CreateSut();

        await Assert.That(sut.Decrypt("plain string value")).IsEqualTo("plain string value");
        await Assert.That(sut.Decrypt(string.Empty)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Decrypt_TamperedCiphertext_ThrowsCryptographicException()
    {
        var sut = CreateSut();
        var cipher = sut.Encrypt("important");

        // Flip one base64 character in the payload, keeping it well-formed base64.
        var payload = cipher[SentinelV1.Length..];
        var corrupt = SentinelV1
            + (payload[0] == 'A' ? 'B' : 'A') + payload[1..];

        await Assert.That(() => sut.Decrypt(corrupt)).Throws<CryptographicException>();
    }

    [Test]
    public async Task Decrypt_KnownPlaintext_DecryptedByDifferentInstanceWithSameKey()
    {
        // Two instances with the same key must be interchangeable — encrypt
        // on the API process, decrypt on the deploy process.
        var key = ValidKeyBase64();
        var encryptor = CreateSut(key);
        var decryptor = CreateSut(key);

        var cipher = encryptor.Encrypt("shared-secret");

        await Assert.That(decryptor.Decrypt(cipher)).IsEqualTo("shared-secret");
    }

    [Test]
    public async Task Decrypt_WithDifferentKey_ThrowsCryptographicException()
    {
        var encryptor = CreateSut(ValidKeyBase64());
        var cipher = encryptor.Encrypt("only-for-key-a");

        var otherKey = new byte[WorkloadEncryptionService.KeyLength];
        for (var i = 0; i < otherKey.Length; i++) otherKey[i] = (byte)(255 - i);
        var decryptor = CreateSut(Convert.ToBase64String(otherKey));

        await Assert.That(() => decryptor.Decrypt(cipher)).Throws<CryptographicException>();
    }

    [Test]
    public async Task Encrypt_MissingKey_ThrowsInvalidOperation()
    {
        var sut = CreateSut(keyBase64: string.Empty);

        await Assert.That(() => sut.Encrypt("anything"))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Decrypt_MissingKey_OnSentinelValue_ThrowsInvalidOperation()
    {
        // Plaintext (no sentinel) is fine without a key — passes through.
        // But sentinel-encrypted values require the key and must fail loudly.
        var sut = CreateSut(keyBase64: string.Empty);

        await Assert.That(sut.Decrypt("plain")).IsEqualTo("plain");
        await Assert.That(() => sut.Decrypt(SentinelV1 + "abc"))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Constructor_InvalidBase64Key_ServiceStartsButThrowsOnUse()
    {
        var sut = CreateSut(keyBase64: "!! not base64 !!");

        await Assert.That(() => sut.Encrypt("x")).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Constructor_WrongKeyLength_ServiceStartsButThrowsOnUse()
    {
        // 16-byte key is valid base64 but wrong length for AES-256.
        var shortKey = Convert.ToBase64String(new byte[16]);
        var sut = CreateSut(keyBase64: shortKey);

        await Assert.That(() => sut.Encrypt("x")).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task IsEncrypted_RecognisesSentinel()
    {
        var sut = CreateSut();

        await Assert.That(sut.IsEncrypted("enc:v1:abc")).IsTrue();
        await Assert.That(sut.IsEncrypted("enc:v2:xyz")).IsTrue(); // future-version safe
        await Assert.That(sut.IsEncrypted("plain")).IsFalse();
        await Assert.That(sut.IsEncrypted(string.Empty)).IsFalse();
    }
}
