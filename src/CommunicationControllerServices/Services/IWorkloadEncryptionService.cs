namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Symmetric encryption / decryption for at-rest secret values in the
/// Communication Controller. Used for Helm <c>ValueOverride</c> entries
/// flagged <c>IsSecret</c>, and for sensitive attributes on
/// <c>HelmRepositoryConfiguration</c> (e.g. registry password).
///
/// Ciphertext is sentinel-prefixed (<c>enc:v1:</c>) so a single field can
/// carry either a plaintext value or an encrypted blob — <see cref="Decrypt"/>
/// returns plaintext unchanged when no sentinel is present, which keeps the
/// migration path simple (and lets local dev work with plain values until
/// secrets are actually filled in).
/// </summary>
public interface IWorkloadEncryptionService
{
    /// <summary>
    /// Encrypts <paramref name="plaintext"/> and returns a string in
    /// <c>enc:v1:&lt;base64&gt;</c> format. Throws when the configured key
    /// is missing or invalid.
    /// </summary>
    string Encrypt(string plaintext);

    /// <summary>
    /// If <paramref name="value"/> starts with the encryption sentinel,
    /// decrypts and returns the plaintext. Otherwise returns the value as-is.
    /// Throws when an encryption sentinel is present but the key is missing,
    /// the ciphertext is malformed, or the GCM tag does not verify (tamper
    /// detection).
    /// </summary>
    string Decrypt(string value);

    /// <summary>
    /// Convenience: true iff <paramref name="value"/> looks like an encrypted
    /// blob (starts with <c>enc:</c>). Does not verify the key or the tag.
    /// </summary>
    bool IsEncrypted(string value);
}
