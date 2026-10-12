using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Secrets;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Symmetric encryption / decryption for at-rest secret values in the Communication Controller,
/// and the controller's single <b>reveal</b> point for <c>Secret</c> attributes (AB#5528 /
/// AB#5537, concept §3.7 and §5.2 phase 2).
///
/// Used for Helm <c>ValueOverride</c> entries flagged <c>IsSecret</c>, for
/// <c>HelmRepositoryConfiguration.Password</c>, for the pipeline service account's
/// <c>ClientSecret</c> and for every credential attribute of a configuration that is shipped to
/// an adapter (<see cref="ConfigurationSecretSerializer" />).
///
/// Ciphertext is sentinel-prefixed (<c>enc:v1:</c> legacy, <c>enc:v2:&lt;kid&gt;:</c> key ring),
/// so a String attribute can carry either a plaintext value or an encrypted blob —
/// <see cref="Decrypt" /> returns plaintext unchanged when no sentinel is present.
/// </summary>
/// <remarks>
/// <para>
/// <b>String AND Secret.</b> Every reader goes through <see cref="Reveal" />, which accepts the raw
/// stored value: a <c>string</c> (a String attribute — plaintext, <c>enc:v1</c> or <c>enc:v2</c>)
/// or an <c>RtSecretValue</c> (a Secret attribute — protected envelope or legacy plaintext). The
/// same code therefore works before and after System.Communication 4.8 turned the credential
/// attributes into <c>Secret</c>.
/// </para>
/// <para>
/// 🔴 Revealed values never reach a log target; failures name the attribute, never the value.
/// </para>
/// </remarks>
public interface IWorkloadEncryptionService
{
    /// <summary>
    /// Encrypts <paramref name="plaintext"/> into the legacy <c>enc:v1:&lt;base64&gt;</c> format
    /// with <c>CommunicationController:InstanceSecretKey</c> (the deprecated <c>encrypt-value</c>
    /// endpoint; the result goes into String slots such as a legacy <c>ValueOverride.Value</c>).
    /// Never writes <c>enc:v2</c> — key-ring envelopes exist only inside Secret attributes. Throws
    /// <see cref="InvalidOperationException" /> when the instance key is not configured.
    /// </summary>
    string Encrypt(string plaintext);

    /// <summary>
    /// Decrypts a String-slot value: a legacy <c>enc:v1</c> blob is decrypted with the instance key
    /// (or the key ring's legacy key); a value without the <c>enc:</c> sentinel is returned as-is.
    /// An <c>enc:v2</c> (or unknown-version) envelope is refused with
    /// <see cref="InvalidOperationException" /> — String slots are no decryption oracle for key-ring
    /// envelopes. Also throws when the key is missing, the ciphertext is malformed, or the GCM tag
    /// does not verify (tamper detection).
    /// </summary>
    string Decrypt(string value);

    /// <summary>
    /// Convenience: true iff <paramref name="value"/> looks like an encrypted
    /// blob (starts with <c>enc:</c>). Does not verify the key or the tag.
    /// </summary>
    bool IsEncrypted(string value);

    /// <summary>
    /// Returns the plaintext of a stored attribute value — the one sanctioned server-side decrypt
    /// path of this service (counted <c>octo.secrets.decrypt</c> for Secret values).
    /// </summary>
    /// <param name="storedValue">
    /// The raw attribute value as read from the entity (<c>GetAttributeValueOrDefault</c>):
    /// <c>null</c>, a <c>string</c> or an <c>RtSecretValue</c>.
    /// </param>
    /// <param name="context">Who reads the value (tags of the decrypt counter)</param>
    /// <returns>
    /// The plaintext, or <c>null</c> when nothing is stored. A <c>Pending</c> secret (write-only,
    /// never expected on a read path) is never revealed and yields <c>null</c>. A Secret value is
    /// decrypted exactly once: a plaintext that itself looks like an envelope is returned as is,
    /// never decrypted again (no decryption oracle). A string is handled like <see cref="Decrypt" />.
    /// </returns>
    /// <exception cref="SecretEncryptionNotConfiguredException">Key material missing for an envelope</exception>
    /// <exception cref="UnknownSecretKeyIdException">The envelope's key id is not in the key ring</exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">Tampered value or wrong key</exception>
    /// <exception cref="ArgumentException">The value is neither a string nor a secret</exception>
    string? Reveal(object? storedValue, SecretAccessContext? context = null);

    /// <summary>
    /// Like <see cref="Reveal" />, but a Secret value that is stored and cannot be read is treated as
    /// NOT SET (decisions 2026-10-06, item 2): an unknown key id, a tampered / wrong-key envelope and
    /// a corrupt value return <c>null</c>, and the engine logs an error naming tenant, CK type,
    /// attribute and key id — never the value (<see cref="ISecretAttributeProtector.RevealOrNull" />).
    /// Used where a missing value degrades gracefully: the configuration shipped to an adapter, Helm
    /// repository passwords, secret value overrides and the adapter credentials of a deployment.
    /// </summary>
    /// <param name="storedValue">The raw attribute value (<c>null</c>, <c>string</c> or <c>RtSecretValue</c>)</param>
    /// <param name="context">Who reads the value (decrypt counter, log tags)</param>
    /// <returns>The plaintext, or <c>null</c> when not set or unreadable</returns>
    /// <exception cref="SecretEncryptionNotConfiguredException">This host has no key ring at all</exception>
    /// <exception cref="LegacyPlaintextSecretRejectedException">Strict mode rejects legacy clear text</exception>
    /// <exception cref="ArgumentException">The value is neither a string nor a secret</exception>
    /// <remarks>A <c>string</c> (String attribute) is handled exactly like <see cref="Reveal" />.</remarks>
    string? RevealOrNull(object? storedValue, SecretAccessContext? context = null);

    /// <summary>
    /// Classifies a stored attribute value without decrypting it (decisions 2026-10-06, item 2) — the
    /// presence check of the deploy guard, the service-account health and the reconcile:
    /// <list type="bullet">
    ///   <item><description><c>RtSecretValue</c>: <see cref="ISecretAttributeProtector.DescribeSecret" />
    ///   of the key ring — <see cref="SecretValueState.KeyMissing" /> when the key id is not in the ring
    ///   (or the host has no key ring); without a registered protector a protected value counts as set;</description></item>
    ///   <item><description><c>string</c> (a String attribute before System.Communication 4.8, i.e.
    ///   legacy clear text): blank or an exact legacy seed placeholder
    ///   (<see cref="SecretAttributeConventions.IsLegacyPlaceholder" />) is not set, an <c>enc:v2</c>
    ///   text is corrupt (not set), anything else is set;</description></item>
    ///   <item><description><c>null</c> or any other type: not set.</description></item>
    /// </list>
    /// </summary>
    /// <param name="storedValue">The raw attribute value</param>
    /// <param name="context">Where the value comes from (log tags only)</param>
    /// <returns>The read description; never the value</returns>
    SecretReadInfo DescribeSecret(object? storedValue, SecretAccessContext? context = null);
}
