using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Sdk.Common.Encryption;
using Microsoft.Extensions.Options;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Communication Controller binding to the shared encryption primitives: the legacy
/// <see cref="IInstanceSecretCrypto" /> (<c>enc:v1</c>, key
/// <see cref="CommunicationControllerOptions.InstanceSecretKey" />, env var
/// <c>OCTO_COMMUNICATIONCONTROLLER__INSTANCESECRETKEY</c>) and the engine's
/// <see cref="ISecretAttributeProtector" /> (<c>enc:v2</c> key ring <c>SecretEncryption</c>,
/// AB#5528 — registered by <c>AddRuntimeEngine()</c>).
/// </summary>
/// <remarks>
/// <para>
/// Wire format of <c>enc:v1</c> (AES-256-GCM, <c>nonce(12) ‖ tag(16) ‖ ciphertext</c>) is defined
/// and tested in <see cref="InstanceSecretCrypto" /> in <c>Meshmakers.Octo.Sdk.Common</c>; the
/// key ring's <c>k1</c> / <c>LegacyV1Key</c> is the same cluster-wide <c>instance_secret_key</c>
/// (concept decision 3), so both formats decrypt with the same key material.
/// </para>
/// <para>
/// <b>Phase 2 (AB#5537).</b> Both keys are optional. A Secret attribute value
/// (<see cref="RtSecretValue" />) is decrypted with the key ring — exactly once: a plaintext that
/// happens to look like an envelope is returned as is, never decrypted again (that would turn any
/// writable Secret attribute into a decryption oracle for envelopes copied from elsewhere). A
/// String slot (a pre-4.8 credential attribute, a legacy <c>ValueOverride.Value</c> with
/// <c>IsSecret</c>) is decrypted only when it holds a legacy <c>enc:v1</c> blob — with the instance
/// key, falling back to the key ring's legacy key; an <c>enc:v2</c> envelope in a String slot is
/// refused. <see cref="Encrypt" /> (the deprecated <c>encrypt-value</c> endpoint) keeps writing
/// <c>enc:v1</c> as before; new secrets belong in Secret attributes (<c>ValueOverride.SecretValue</c>).
/// </para>
/// </remarks>
public sealed class WorkloadEncryptionService : IWorkloadEncryptionService
{
    internal const int KeyLength = 32;       // AES-256

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly IInstanceSecretCrypto _crypto;
    private readonly ISecretAttributeProtector? _protector;
    private readonly byte[]? _key;
    private readonly string? _keyConfigurationError;

    /// <summary>
    /// Constructor — reads the legacy master key once from
    /// <see cref="CommunicationControllerOptions.InstanceSecretKey"/> and decodes it Base64.
    /// </summary>
    /// <param name="options">Controller options carrying the legacy instance key</param>
    /// <param name="crypto">The legacy <c>enc:v1</c> primitive</param>
    /// <param name="protector">The engine's key ring (<c>enc:v2</c>); optional for hosts without the engine</param>
    public WorkloadEncryptionService(
        IOptions<CommunicationControllerOptions> options,
        IInstanceSecretCrypto crypto,
        ISecretAttributeProtector? protector = null)
    {
        _crypto = crypto;
        _protector = protector;
        var raw = options.Value.InstanceSecretKey;
        if (string.IsNullOrWhiteSpace(raw))
        {
            _key = null;
            _keyConfigurationError =
                "CommunicationController:InstanceSecretKey is not configured. encrypt-value and legacy enc:v1 values " +
                "need a base64-encoded 32-byte key via OCTO_COMMUNICATIONCONTROLLER__INSTANCESECRETKEY (enc:v1 values " +
                "can also be read through the SecretEncryption key ring's LegacyV1Key).";
            return;
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(raw);
        }
        catch (FormatException)
        {
            _key = null;
            _keyConfigurationError =
                "CommunicationController:InstanceSecretKey is not valid base64.";
            return;
        }

        if (decoded.Length != KeyLength)
        {
            _key = null;
            _keyConfigurationError =
                $"CommunicationController:InstanceSecretKey must decode to {KeyLength} bytes; got {decoded.Length}.";
            return;
        }

        _key = decoded;
    }

    /// <inheritdoc />
    public string Encrypt(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        // AB#5537: deliberately enc:v1 only, as before the key ring. The result is written into
        // plain String slots (legacy ValueOverride.Value with IsSecret); enc:v2 envelopes live only
        // inside Secret attributes, where the engine writes them itself.
        if (_key is null)
        {
            throw new InvalidOperationException(_keyConfigurationError);
        }

        return _crypto.Encrypt(_key, plaintext);
    }

    /// <inheritdoc />
    public string Decrypt(string value)
    {
        return Decrypt(value, null);
    }

    /// <inheritdoc />
    public bool IsEncrypted(string value) => _crypto.IsEncrypted(value);

    /// <inheritdoc />
    public string? Reveal(object? storedValue, SecretAccessContext? context = null)
    {
        switch (storedValue)
        {
            case null:
                return null;
            case string legacyString:
                // A String attribute (phase 2) or a legacy value handed out un-normalised.
                return Decrypt(legacyString, context);
            case RtSecretValue { IsPending: true }:
                // Pending = write-only plaintext that has not been stored yet. It never leaves this
                // service: a read path that meets one has a bug, and the safe answer is "not set".
                Logger.Warn(
                    "[{TenantId}] Refused to reveal a pending (unsaved) secret value of attribute '{AttributeName}' ({CkTypeId})",
                    context?.TenantId, context?.AttributeName, context?.CkTypeId);
                return null;
            case RtSecretValue secret:
            {
                // Protected: decrypted with the key ring. LegacyPlaintext (a string found in a Secret
                // slot — clear text or enc:v1 written before the attribute became Secret): returned
                // or decrypted with the ring's LegacyV1Key, and counted as a plaintext read — the
                // sweep's verification signal. The legacy text is only reachable through the
                // protector, so a legacy enc:v1 value needs SecretEncryption:LegacyV1Key.
                // 🔴 Exactly one decrypt. A revealed plaintext that looks like an envelope is the
                // value the author typed and is returned as is — decrypting it again would let
                // anyone who can write a Secret attribute have a copied envelope decrypted into a
                // deployed workload (decryption oracle).
                return RequireProtector().Unprotect(secret, context);
            }
            default:
                throw new ArgumentException(
                    $"A stored secret must be a string or an RtSecretValue, not '{storedValue.GetType().Name}'.",
                    nameof(storedValue));
        }
    }

    /// <inheritdoc />
    public string? RevealOrNull(object? storedValue, SecretAccessContext? context = null)
    {
        if (storedValue is not RtSecretValue { IsPending: false } secret)
        {
            return Reveal(storedValue, context);
        }

        var protector = RequireProtector();
        var info = protector.DescribeSecret(secret, context);
        if (info.KeyMissing && protector.IsConfigured)
        {
            // Decisions 2026-10-06, item 2: kept encrypted, readable again once the key is added.
            Logger.Error(
                "[{TenantId}] Secret attribute '{AttributeName}' ({CkTypeId}) is encrypted with key id '{KeyId}', which is not in this controller's key ring; it is treated as not set",
                context?.TenantId, context?.AttributeName, context?.CkTypeId, info.KeyId);
            return null;
        }

        if (info.State == SecretValueState.NotSet)
        {
            // Null, empty, a legacy placeholder or corrupt (the engine warned about the latter).
            return null;
        }

        // Tampered / wrong-key envelope -> null as well (the engine counts octo.secrets.unreadable).
        // A host without key ring still throws. Exactly one decrypt, as in Reveal.
        var plaintext = protector.RevealOrNull(secret, context);
        if (plaintext == null)
        {
            Logger.Error(
                "[{TenantId}] Secret attribute '{AttributeName}' ({CkTypeId}) could not be decrypted (damaged, or encrypted with a different key under key id '{KeyId}'); it is treated as not set",
                context?.TenantId, context?.AttributeName, context?.CkTypeId, info.KeyId ?? "-");
        }

        return plaintext;
    }

    /// <inheritdoc />
    public SecretReadInfo DescribeSecret(object? storedValue, SecretAccessContext? context = null)
    {
        switch (storedValue)
        {
            case RtSecretValue secret:
                return _protector?.DescribeSecret(secret, context) ?? SecretValueStates.Describe(secret, null);
            case string text when string.IsNullOrWhiteSpace(text) || SecretAttributeConventions.IsLegacyPlaceholder(text):
                return new SecretReadInfo(SecretValueState.NotSet, SecretStorageForm.NotSet, null, null);
            case string text when text.StartsWith(SecretEnvelope.PrefixV1, StringComparison.Ordinal):
                return new SecretReadInfo(SecretValueState.Set, SecretStorageForm.EncV1, null, null);
            case string text when IsEncrypted(text):
                // enc:v2 (or unknown) in a String slot: refused by Decrypt, never readable.
                return new SecretReadInfo(SecretValueState.NotSet, SecretStorageForm.Corrupt, null, null);
            case string:
                return new SecretReadInfo(SecretValueState.Set, SecretStorageForm.Plaintext, null, null);
            default:
                return new SecretReadInfo(SecretValueState.NotSet, SecretStorageForm.NotSet, null, null);
        }
    }

    private string Decrypt(string value, SecretAccessContext? context)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!IsEncrypted(value))
        {
            return value;
        }

        if (!value.StartsWith(SecretEnvelope.PrefixV1, StringComparison.Ordinal))
        {
            // enc:v2 (or an unknown version) in a String slot: nothing legitimate writes one there
            // (encrypt-value writes enc:v1, Secret attributes are RtSecretValue). Refused rather
            // than decrypted, so a String attribute is no decryption oracle for key-ring envelopes.
            throw new InvalidOperationException(
                "A String attribute holds an 'enc:v2' (or unknown) envelope; only legacy 'enc:v1' values are " +
                "decrypted from String attributes. Store the secret in a Secret attribute (e.g. ValueOverride.SecretValue).");
        }

        if (_key is null)
        {
            if (_protector != null)
            {
                // The key ring's LegacyV1Key is the same instance key (concept decision 3).
                return _protector.Unprotect(value, context);
            }

            throw new InvalidOperationException(_keyConfigurationError);
        }

        return _crypto.Decrypt(_key, value);
    }

    private ISecretAttributeProtector RequireProtector()
    {
        return _protector ?? throw new SecretEncryptionNotConfiguredException(
            "Cannot decrypt an 'enc:v2' secret: the runtime engine's key ring (ISecretAttributeProtector) is not " +
            "registered in this host.");
    }
}
