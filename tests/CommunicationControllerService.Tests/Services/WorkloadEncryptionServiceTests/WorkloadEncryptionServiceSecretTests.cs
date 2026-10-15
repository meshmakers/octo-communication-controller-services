using System.Security.Cryptography;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Sdk.Common.Encryption;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.WorkloadEncryptionServiceTests;

/// <summary>
/// AB#5537 — the controller's encryption service on top of the engine key ring
/// (<see cref="ISecretAttributeProtector" />): encrypt-value keeps writing enc:v1, String slots
/// decrypt only enc:v1, and <see cref="IWorkloadEncryptionService.Reveal" /> for String and Secret values.
/// All keys are generated per test.
/// </summary>
internal class WorkloadEncryptionServiceSecretTests
{
    private const string Plaintext = "s3cret-value-äöü";

    // ---------------------------------------------------------------- Encrypt / Decrypt

    [Test]
    public async Task Encrypt_WithKeyRing_StillWritesEncV1WithTheInstanceKey()
    {
        // encrypt-value output goes into String slots; enc:v2 lives only inside Secret attributes.
        var secrets = TestSecretEncryption.Create();

        var cipher = secrets.EncryptionService.Encrypt(Plaintext);

        await Assert.That(cipher).StartsWith("enc:v1:");
        await Assert.That(secrets.EncryptionService.Decrypt(cipher)).IsEqualTo(Plaintext);
    }

    [Test]
    public async Task Encrypt_WithKeyRingButNoInstanceKey_Throws()
    {
        var secrets = TestSecretEncryption.Create(withInstanceKey: false);

        var exception = Assert.Throws<InvalidOperationException>(() => secrets.EncryptionService.Encrypt("x"));

        await Assert.That(exception.Message).Contains("InstanceSecretKey");
    }

    [Test]
    public async Task Encrypt_WithoutKeyRing_FallsBackToEncV1WithTheInstanceKey()
    {
        var secrets = TestSecretEncryption.Create(withKeyRing: false);

        var cipher = secrets.EncryptionService.Encrypt(Plaintext);

        await Assert.That(cipher).StartsWith("enc:v1:");
        await Assert.That(secrets.EncryptionService.Decrypt(cipher)).IsEqualTo(Plaintext);
    }

    [Test]
    public async Task Encrypt_WithNeitherKey_ThrowsInvalidOperationNamingBothSettings()
    {
        var secrets = TestSecretEncryption.Create(withKeyRing: false, withInstanceKey: false);

        var exception = Assert.Throws<InvalidOperationException>(() => secrets.EncryptionService.Encrypt("x"));

        await Assert.That(exception.Message).Contains("SecretEncryption");
        await Assert.That(exception.Message).Contains("InstanceSecretKey");
    }

    [Test]
    public async Task Decrypt_EncV1_WithoutInstanceKey_UsesTheKeyRingsLegacyKey()
    {
        // Decision 3: LegacyV1Key IS the instance key — an installation that dropped the old
        // CommunicationController setting keeps reading its enc:v1 values.
        var secrets = TestSecretEncryption.Create();
        var legacyCipher = secrets.EncryptV1(Plaintext);
        var ringOnly = new WorkloadEncryptionService(
            Microsoft.Extensions.Options.Options.Create(new CommunicationControllerOptions()),
            new InstanceSecretCrypto(secrets.Protector), secrets.Protector);

        await Assert.That(ringOnly.Decrypt(legacyCipher)).IsEqualTo(Plaintext);
    }

    [Test]
    public async Task Decrypt_EncV2EnvelopeInAString_IsRefused_EvenWithTheKeyRing()
    {
        // No decryption oracle: a key-ring envelope copied into a String slot is never decrypted.
        var secrets = TestSecretEncryption.Create();
        var envelope = secrets.Protector.Protect(Plaintext).Envelope!;

        var exception = Assert.Throws<InvalidOperationException>(() => secrets.EncryptionService.Decrypt(envelope));

        await Assert.That(exception.Message).DoesNotContain(Plaintext);
        await Assert.That(() => secrets.EncryptionService.Reveal(envelope)).Throws<InvalidOperationException>();
    }

    // ---------------------------------------------------------------- Reveal

    [Test]
    public async Task Reveal_NullAndPlainStrings_PassThrough()
    {
        var sut = TestSecretEncryption.Create().EncryptionService;

        await Assert.That(sut.Reveal(null)).IsNull();
        await Assert.That(sut.Reveal(string.Empty)).IsEqualTo(string.Empty);
        await Assert.That(sut.Reveal(Plaintext)).IsEqualTo(Plaintext);
    }

    [Test]
    public async Task Reveal_LegacyEncV1String_IsDecrypted()
    {
        // A String attribute (before System.Communication 4.8, or a legacy ValueOverride.Value
        // with IsSecret) holds enc:v1 — both written by hand and by encrypt-value.
        var secrets = TestSecretEncryption.Create();

        await Assert.That(secrets.EncryptionService.Reveal(secrets.EncryptV1(Plaintext))).IsEqualTo(Plaintext);
        await Assert.That(secrets.EncryptionService.Reveal(secrets.EncryptionService.Encrypt(Plaintext)))
            .IsEqualTo(Plaintext);
    }

    [Test]
    public async Task Reveal_ProtectedSecretValue_IsDecrypted()
    {
        var secrets = TestSecretEncryption.Create();
        var stored = secrets.Protector.Protect(Plaintext);

        await Assert.That(secrets.EncryptionService.Reveal(stored,
            new SecretAccessContext("tenant", "System.Communication/SftpConfiguration", "Password")))
            .IsEqualTo(Plaintext);
    }

    [Test]
    public async Task Reveal_LegacySecretValues_ClearTextAndEncV1_AreRevealed()
    {
        // A string still sitting in a Secret slot (before the encrypt sweep).
        var secrets = TestSecretEncryption.Create();

        await Assert.That(secrets.EncryptionService.Reveal(RtSecretValue.LegacyPlaintext(Plaintext)))
            .IsEqualTo(Plaintext);
        await Assert.That(secrets.EncryptionService.Reveal(RtSecretValue.LegacyPlaintext(secrets.EncryptV1(Plaintext))))
            .IsEqualTo(Plaintext);
    }

    [Test]
    public async Task Reveal_PendingSecretValue_IsNeverRevealed()
    {
        var sut = TestSecretEncryption.Create().EncryptionService;

        await Assert.That(sut.Reveal(RtSecretValue.Pending(Plaintext))).IsNull();
    }

    [Test]
    public async Task Reveal_SecretWhosePlaintextIsAnEnvelope_IsReturnedVerbatim_NeverDecryptedAgain()
    {
        // Decrypting twice would turn any writable Secret attribute into a decryption oracle for
        // envelopes copied from elsewhere (another attribute, an export, an enc:v1 String).
        var secrets = TestSecretEncryption.Create();
        var v1 = secrets.EncryptV1(Plaintext);
        var v2 = secrets.Protector.Protect(Plaintext).Envelope!;

        using var _ = Assert.Multiple();
        await Assert.That(secrets.EncryptionService.Reveal(secrets.Protector.Protect(v1))).IsEqualTo(v1);
        await Assert.That(secrets.EncryptionService.Reveal(secrets.Protector.Protect(v2))).IsEqualTo(v2);
    }

    [Test]
    public async Task Reveal_PlaintextThatMerelyStartsWithEnc_IsNotUnwrapped()
    {
        var secrets = TestSecretEncryption.Create();
        var stored = secrets.Protector.Protect("enc:not-an-envelope");

        await Assert.That(secrets.EncryptionService.Reveal(stored)).IsEqualTo("enc:not-an-envelope");
    }

    [Test]
    public async Task Reveal_SecretValue_WithoutKeyRing_ThrowsNotConfigured()
    {
        var withRing = TestSecretEncryption.Create();
        var stored = withRing.Protector.Protect(Plaintext);
        var legacyOnly = new WorkloadEncryptionService(withRing.ControllerOptions, new InstanceSecretCrypto());

        await Assert.That(() => legacyOnly.Reveal(stored)).Throws<SecretEncryptionNotConfiguredException>();
    }

    [Test]
    public async Task Reveal_UnknownKeyId_ThrowsUnknownSecretKeyId()
    {
        var secrets = TestSecretEncryption.Create();
        var foreign = RtSecretValue.Protected("enc:v2:k9:" +
                                              Convert.ToBase64String(RandomNumberGenerator.GetBytes(40))
                                                  .Replace('+', '-').Replace('/', '_').TrimEnd('='));

        await Assert.That(() => secrets.EncryptionService.Reveal(foreign)).Throws<UnknownSecretKeyIdException>();
    }

    [Test]
    public async Task Reveal_UnsupportedValueType_ThrowsArgumentExceptionWithoutTheValue()
    {
        var sut = TestSecretEncryption.Create().EncryptionService;

        var exception = Assert.Throws<ArgumentException>(() => sut.Reveal(424242));

        await Assert.That(exception.Message).DoesNotContain("424242");
    }

    // ---------------------------------------------------------------- DI (InstanceSecretCrypto)

    [Test]
    public async Task InstanceSecretCrypto_ResolvedFromDi_DecryptsEncV1AndRefusesEncV2()
    {
        // Program.cs registers AddSingleton<IInstanceSecretCrypto, InstanceSecretCrypto>() next to
        // AddRuntimeEngine(). DI must still resolve it (protector constructor), but since the
        // decryption-oracle hardening (AB#5534) it decrypts enc:v1 only; enc:v2 envelopes belong to
        // Secret attributes and are refused without echoing the value.
        var secrets = TestSecretEncryption.Create();
        var crypto = secrets.ServiceProvider.GetRequiredService<IInstanceSecretCrypto>();
        var key = Convert.FromBase64String(secrets.InstanceKeyBase64!);
        var envelope = secrets.Protector.Protect(Plaintext).Envelope!;

        await Assert.That(crypto.Decrypt(key, secrets.EncryptV1(Plaintext))).IsEqualTo(Plaintext);

        var exception = Assert.Throws<InvalidOperationException>(() => crypto.Decrypt(key, envelope));
        await Assert.That(exception.Message).DoesNotContain(envelope);
        await Assert.That(exception.Message).DoesNotContain(Plaintext);
    }

    // ---------------------------------------------------------------- RevealOrNull / DescribeSecret (decisions 2026-10-06, item 2)

    [Test]
    public async Task RevealOrNull_UnknownKeyId_IsNotSet()
    {
        var secrets = TestSecretEncryption.Create();
        var foreign = TestSecretEncryption.Create(keyId: "k9").Protector.Protect(Plaintext);

        await Assert.That(secrets.EncryptionService.RevealOrNull(foreign,
            new SecretAccessContext("tenant", "System.Communication/SftpConfiguration", "Password"))).IsNull();
    }

    [Test]
    public async Task RevealOrNull_TamperedValue_IsNotSet()
    {
        var secrets = TestSecretEncryption.Create();
        var wrongKey = TestSecretEncryption.Create().Protector.Protect(Plaintext); // same key id k1, other key

        await Assert.That(secrets.EncryptionService.RevealOrNull(wrongKey)).IsNull();
    }

    [Test]
    public async Task RevealOrNull_ReadableValues_AreRevealedLikeReveal()
    {
        var secrets = TestSecretEncryption.Create();

        using var _ = Assert.Multiple();
        await Assert.That(secrets.EncryptionService.RevealOrNull(secrets.Protector.Protect(Plaintext)))
            .IsEqualTo(Plaintext);
        await Assert.That(secrets.EncryptionService.RevealOrNull(secrets.Protector.Protect("<insert secret here>")))
            .IsEqualTo("<insert secret here>");
        await Assert.That(secrets.EncryptionService.RevealOrNull(RtSecretValue.LegacyPlaintext(Plaintext)))
            .IsEqualTo(Plaintext);
        await Assert.That(secrets.EncryptionService.RevealOrNull(secrets.EncryptV1(Plaintext))).IsEqualTo(Plaintext);
        await Assert.That(secrets.EncryptionService.RevealOrNull(null)).IsNull();
        await Assert.That(secrets.EncryptionService.RevealOrNull(RtSecretValue.Pending(Plaintext))).IsNull();
    }

    [Test]
    public async Task RevealOrNull_WithoutKeyRing_StillThrowsNotConfigured()
    {
        var withRing = TestSecretEncryption.Create();
        var stored = withRing.Protector.Protect(Plaintext);
        var unconfigured = TestSecretEncryption.Create(withKeyRing: false);
        var legacyOnly = new WorkloadEncryptionService(withRing.ControllerOptions, new InstanceSecretCrypto());

        using var _ = Assert.Multiple();
        await Assert.That(() => unconfigured.EncryptionService.RevealOrNull(stored))
            .Throws<SecretEncryptionNotConfiguredException>();
        await Assert.That(() => legacyOnly.RevealOrNull(stored)).Throws<SecretEncryptionNotConfiguredException>();
    }

    [Test]
    public async Task DescribeSecret_ClassifiesWithoutDecrypting()
    {
        var secrets = TestSecretEncryption.Create();
        var foreign = TestSecretEncryption.Create(keyId: "k9").Protector.Protect(Plaintext);

        using var _ = Assert.Multiple();
        await Assert.That(secrets.EncryptionService.DescribeSecret(secrets.Protector.Protect(Plaintext)).Form)
            .IsEqualTo(SecretStorageForm.EncV2);
        await Assert.That(secrets.EncryptionService.DescribeSecret(foreign).State)
            .IsEqualTo(SecretValueState.KeyMissing);
        await Assert.That(secrets.EncryptionService.DescribeSecret(Plaintext).Form)
            .IsEqualTo(SecretStorageForm.Plaintext);
        await Assert.That(secrets.EncryptionService.DescribeSecret(secrets.EncryptV1(Plaintext)).Form)
            .IsEqualTo(SecretStorageForm.EncV1);
        await Assert.That(secrets.EncryptionService.DescribeSecret(secrets.Protector.Protect(Plaintext).Envelope).Form)
            .IsEqualTo(SecretStorageForm.Corrupt);
        await Assert.That(secrets.EncryptionService.DescribeSecret("TODO_SET_PASSWORD").IsSet).IsFalse();
        await Assert.That(secrets.EncryptionService.DescribeSecret(" ").IsSet).IsFalse();
        await Assert.That(secrets.EncryptionService.DescribeSecret(null).IsSet).IsFalse();
    }
}
