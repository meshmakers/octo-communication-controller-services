using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Sdk.Common.Encryption;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Communication Controller binding to the shared
/// <see cref="IInstanceSecretCrypto" /> primitive. Reads the per-service key from
/// <see cref="CommunicationControllerOptions" /> (env var
/// <c>OCTO_COMMUNICATIONCONTROLLER__INSTANCESECRETKEY</c>), Base64-decodes it once at
/// construction, then delegates every encrypt/decrypt to the cross-service primitive.
/// </summary>
/// <remarks>
/// Wire format (<c>enc:v1:</c> sentinel, AES-256-GCM,
/// <c>nonce(12) ‖ tag(16) ‖ ciphertext</c>) is defined and tested in
/// <see cref="InstanceSecretCrypto" /> in <c>Meshmakers.Octo.Sdk.Common</c>. With the
/// cluster-wide <c>global.instanceSecretKey</c> Helm value materialised into both
/// <c>OCTO_COMMUNICATIONCONTROLLER__INSTANCESECRETKEY</c> and the AI Adapter's
/// <c>OCTO_AIENCRYPTION__INSTANCESECRETKEY</c>, either service can decrypt ciphertext
/// produced by the other.
/// </remarks>
public sealed class WorkloadEncryptionService : IWorkloadEncryptionService
{
    internal const int KeyLength = 32;       // AES-256

    private readonly IInstanceSecretCrypto _crypto;
    private readonly byte[]? _key;
    private readonly string? _keyConfigurationError;

    /// <summary>
    /// Constructor — reads the master key once from
    /// <see cref="CommunicationControllerOptions.InstanceSecretKey"/> and decodes it Base64.
    /// </summary>
    public WorkloadEncryptionService(
        IOptions<CommunicationControllerOptions> options,
        IInstanceSecretCrypto crypto)
    {
        _crypto = crypto;
        var raw = options.Value.InstanceSecretKey;
        if (string.IsNullOrWhiteSpace(raw))
        {
            _key = null;
            _keyConfigurationError =
                "CommunicationController:InstanceSecretKey is not configured. " +
                "Provide a base64-encoded 32-byte key (e.g. via " +
                "OCTO_COMMUNICATIONCONTROLLER__INSTANCESECRETKEY).";
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
        if (_key is null)
        {
            throw new InvalidOperationException(_keyConfigurationError);
        }

        return _crypto.Encrypt(_key, plaintext);
    }

    /// <inheritdoc />
    public string Decrypt(string value)
    {
        if (!IsEncrypted(value))
        {
            return value;
        }

        if (_key is null)
        {
            throw new InvalidOperationException(_keyConfigurationError);
        }

        return _crypto.Decrypt(_key, value);
    }

    /// <inheritdoc />
    public bool IsEncrypted(string value) => _crypto.IsEncrypted(value);
}
