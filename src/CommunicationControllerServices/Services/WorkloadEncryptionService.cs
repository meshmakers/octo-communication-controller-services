using System.Security.Cryptography;
using System.Text;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// AES-256-GCM symmetric encryption with a single instance-wide key.
/// Ciphertext layout (after the <c>enc:v1:</c> sentinel and Base64 decode):
/// <c>nonce(12) || tag(16) || ciphertext(N)</c>.
/// </summary>
public sealed class WorkloadEncryptionService : IWorkloadEncryptionService
{
    internal const string SentinelV1 = "enc:v1:";
    internal const int KeyLength = 32;       // AES-256
    internal const int NonceLength = 12;     // GCM standard
    internal const int TagLength = 16;       // GCM standard

    private readonly byte[]? _key;
    private readonly string? _keyConfigurationError;

    /// <summary>
    /// Constructor — reads the master key once from
    /// <see cref="CommunicationControllerOptions.InstanceSecretKey"/>.
    /// </summary>
    public WorkloadEncryptionService(IOptions<CommunicationControllerOptions> options)
    {
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

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagLength];

        using var aes = new AesGcm(_key, TagLength);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        // Layout: nonce || tag || ciphertext
        var combined = new byte[NonceLength + TagLength + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, NonceLength);
        Buffer.BlockCopy(tag, 0, combined, NonceLength, TagLength);
        Buffer.BlockCopy(ciphertext, 0, combined, NonceLength + TagLength, ciphertext.Length);

        return SentinelV1 + Convert.ToBase64String(combined);
    }

    /// <inheritdoc />
    public string Decrypt(string value)
    {
        if (!IsEncrypted(value))
        {
            return value;
        }

        if (!value.StartsWith(SentinelV1, StringComparison.Ordinal))
        {
            throw new CryptographicException(
                $"Unsupported encryption version. Expected sentinel '{SentinelV1}'.");
        }

        if (_key is null)
        {
            throw new InvalidOperationException(_keyConfigurationError);
        }

        var payload = value[SentinelV1.Length..];
        byte[] combined;
        try
        {
            combined = Convert.FromBase64String(payload);
        }
        catch (FormatException e)
        {
            throw new CryptographicException("Encrypted value is not valid base64.", e);
        }

        if (combined.Length < NonceLength + TagLength)
        {
            throw new CryptographicException("Encrypted value is truncated.");
        }

        var nonce = new byte[NonceLength];
        var tag = new byte[TagLength];
        var ciphertext = new byte[combined.Length - NonceLength - TagLength];
        Buffer.BlockCopy(combined, 0, nonce, 0, NonceLength);
        Buffer.BlockCopy(combined, NonceLength, tag, 0, TagLength);
        Buffer.BlockCopy(combined, NonceLength + TagLength, ciphertext, 0, ciphertext.Length);

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(_key, TagLength);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return Encoding.UTF8.GetString(plaintext);
    }

    /// <inheritdoc />
    public bool IsEncrypted(string value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith("enc:", StringComparison.Ordinal);
}
