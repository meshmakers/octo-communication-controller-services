using System.Text.Json;
using System.Text.Json.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     Why <see cref="IExecutionEnvelope.TryOpen{T}" /> refused a stored value. Logged by name, never with the value.
/// </summary>
public enum ExecutionEnvelopeRejection
{
    /// <summary>The value was opened.</summary>
    None = 0,

    /// <summary>Nothing is stored.</summary>
    Missing,

    /// <summary>The value is not an <c>enc:v2</c> envelope (plain text, <c>enc:v1</c>, anything else).</summary>
    NotAnEnvelope,

    /// <summary>The controller has no usable key ring.</summary>
    NoKey,

    /// <summary>The key id is unknown or the authentication tag does not verify (edited, wrong key).</summary>
    Unreadable,

    /// <summary>The decrypted content is not a well-formed envelope of this format.</summary>
    Malformed,

    /// <summary>The envelope was sealed for another purpose.</summary>
    WrongPurpose,

    /// <summary>The envelope was sealed for another tenant.</summary>
    WrongTenant,

    /// <summary>The envelope was sealed for another execution (a copied value).</summary>
    WrongExecution
}

/// <summary>
///     AB#7472 — a value that the controller writes onto a <c>PipelineExecution</c> and later trusts, sealed so that
///     nobody who can edit the entity through a generic write surface can change or transplant it.
/// </summary>
/// <remarks>
///     <para>
///         The attributes of a queued execution are ordinary, writable attributes. What the controller reads back from
///         them at lease grant (the invoker, later the method-invocation marker of AB#7461) therefore has to prove that
///         the controller itself wrote it, for exactly this tenant and exactly this execution.
///     </para>
///     <para>
///         <b>Format.</b> The payload is wrapped into <c>{v, p, t, e, d}</c> (format version, purpose, tenant id,
///         execution RtId, data) and encrypted with the engine key ring (<c>enc:v2:&lt;kid&gt;</c>, AES-256-GCM).
///         The key ring authenticates only its own header as associated data, so the binding is part of the
///         authenticated plaintext and checked after decryption; the effect is the same as associated data: a flipped
///         bit, a foreign key, another purpose, another tenant or another execution does not open. Key rotation works
///         as for every <c>enc:v2</c> value.
///     </para>
///     <para>
///         <b>Fail closed.</b> Every refusal returns <c>false</c> with a reason; the caller treats it as "nothing was
///         stored". Plain text is never accepted, so a value written through a generic mutation cannot be opened.
///     </para>
///     <para>
///         🔴 Opened plaintext never reaches a log; refusals log the reason, tenant and execution only.
///     </para>
/// </remarks>
public interface IExecutionEnvelope
{
    /// <summary>
    ///     Seals <paramref name="payload" /> for one tenant and one execution. Returns <c>null</c> (and logs a warning)
    ///     when the controller has no key ring: nothing is stored then, which the reader treats as absent.
    /// </summary>
    /// <param name="purpose">What the value is (see <see cref="ExecutionEnvelopePurposes" />)</param>
    /// <param name="payload">The data; serialized with System.Text.Json</param>
    /// <param name="tenantId">Tenant of the execution</param>
    /// <param name="executionRtId">RtId of the execution the value is stored on</param>
    string? Seal<T>(string purpose, T payload, string tenantId, OctoObjectId executionRtId);

    /// <summary>
    ///     Opens a stored value if, and only if, it was sealed by this controller's key ring for exactly this
    ///     purpose, tenant and execution.
    /// </summary>
    bool TryOpen<T>(string purpose, string? stored, string tenantId, OctoObjectId executionRtId, out T? payload,
        out ExecutionEnvelopeRejection rejection);

    /// <summary>
    ///     Opens a value that was sealed for <paramref name="fromExecutionRtId" /> and seals it again for
    ///     <paramref name="toExecutionRtId" /> (a re-queued work item is a NEW execution). Never copies the old
    ///     ciphertext. Returns <c>null</c> when the old value does not open or cannot be sealed again.
    /// </summary>
    string? Reseal<T>(string purpose, string? stored, string tenantId, OctoObjectId fromExecutionRtId,
        OctoObjectId toExecutionRtId);
}

/// <summary>The purposes an <see cref="IExecutionEnvelope" /> is sealed for. Part of the authenticated content.</summary>
public static class ExecutionEnvelopePurposes
{
    /// <summary>The invoker of a queued work item (AB#7472): principal, trust level, token.</summary>
    public const string QueuedCaller = "queued-caller";
}

/// <inheritdoc />
public sealed class ExecutionEnvelope(ISecretAttributeProtector protector) : IExecutionEnvelope
{
    private const int FormatVersion = 1;

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = null };

    /// <inheritdoc />
    public string? Seal<T>(string purpose, T payload, string tenantId, OctoObjectId executionRtId)
    {
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        ArgumentException.ThrowIfNullOrEmpty(tenantId);

        if (!protector.IsConfigured || protector.ActiveKeyId is null)
        {
            Logger.Warn(
                "[{TenantId}] A '{Purpose}' value for execution '{ExecutionRtId}' was NOT stored: the controller has " +
                "no secret key ring (SecretEncryption). The work item runs without it.",
                tenantId, purpose, executionRtId);
            return null;
        }

        try
        {
            var wrapper = new Wrapper
            {
                Version = FormatVersion,
                Purpose = purpose,
                TenantId = tenantId,
                ExecutionRtId = executionRtId.ToString(),
                Data = JsonSerializer.SerializeToElement(payload, JsonOptions)
            };
            return protector.Protect(JsonSerializer.Serialize(wrapper, JsonOptions)).Envelope;
        }
        catch (Exception e) when (e is SecretEncryptionNotConfiguredException or JsonException)
        {
            Logger.Warn(e,
                "[{TenantId}] A '{Purpose}' value for execution '{ExecutionRtId}' could not be sealed and was not stored",
                tenantId, purpose, executionRtId);
            return null;
        }
    }

    /// <inheritdoc />
    public bool TryOpen<T>(string purpose, string? stored, string tenantId, OctoObjectId executionRtId,
        out T? payload, out ExecutionEnvelopeRejection rejection)
    {
        payload = default;

        if (string.IsNullOrEmpty(stored))
        {
            rejection = ExecutionEnvelopeRejection.Missing;
            return false;
        }

        // Strictly an enc:v2 envelope. protector.Unprotect(string) would accept plain text as "legacy clear text"
        // and enc:v1 with the legacy key; neither may ever open a sealed value.
        if (!protector.TryParseEnvelope(stored, out var info) || info.Version != SecretEnvelope.CurrentVersion)
        {
            rejection = ExecutionEnvelopeRejection.NotAnEnvelope;
            return false;
        }

        if (!protector.IsConfigured)
        {
            rejection = ExecutionEnvelopeRejection.NoKey;
            return false;
        }

        string json;
        try
        {
            json = protector.Unprotect(stored, new SecretAccessContext(tenantId, null, "SealedCaller",
                "CommunicationController"));
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException
                                      or UnknownSecretKeyIdException
                                      or SecretEncryptionNotConfiguredException
                                      or LegacyPlaintextSecretRejectedException
                                      or SecretEnvelopeNotAllowedException)
        {
            rejection = e is SecretEncryptionNotConfiguredException
                ? ExecutionEnvelopeRejection.NoKey
                : ExecutionEnvelopeRejection.Unreadable;
            return false;
        }

        Wrapper? wrapper;
        try
        {
            wrapper = JsonSerializer.Deserialize<Wrapper>(json, JsonOptions);
        }
        catch (JsonException)
        {
            // Some other decrypted secret copied into the attribute (decryption is only ever used to compare, the
            // content is never returned or logged).
            rejection = ExecutionEnvelopeRejection.Malformed;
            return false;
        }

        if (wrapper is null || wrapper.Version != FormatVersion || wrapper.Purpose is null ||
            wrapper.TenantId is null || wrapper.ExecutionRtId is null)
        {
            rejection = ExecutionEnvelopeRejection.Malformed;
            return false;
        }

        if (!string.Equals(wrapper.Purpose, purpose, StringComparison.Ordinal))
        {
            rejection = ExecutionEnvelopeRejection.WrongPurpose;
            return false;
        }

        if (!string.Equals(wrapper.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
        {
            rejection = ExecutionEnvelopeRejection.WrongTenant;
            return false;
        }

        if (!string.Equals(wrapper.ExecutionRtId, executionRtId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            rejection = ExecutionEnvelopeRejection.WrongExecution;
            return false;
        }

        try
        {
            payload = wrapper.Data.Deserialize<T>(JsonOptions);
        }
        catch (JsonException)
        {
            rejection = ExecutionEnvelopeRejection.Malformed;
            return false;
        }

        rejection = ExecutionEnvelopeRejection.None;
        return payload is not null;
    }

    /// <inheritdoc />
    public string? Reseal<T>(string purpose, string? stored, string tenantId, OctoObjectId fromExecutionRtId,
        OctoObjectId toExecutionRtId)
    {
        if (!TryOpen<T>(purpose, stored, tenantId, fromExecutionRtId, out var payload, out var rejection))
        {
            if (rejection != ExecutionEnvelopeRejection.Missing)
            {
                Logger.Warn(
                    "[{TenantId}] The '{Purpose}' value of execution '{ExecutionRtId}' did not verify ({Reason}) and is " +
                    "not carried onto the re-queued execution",
                    tenantId, purpose, fromExecutionRtId, rejection);
            }

            return null;
        }

        return Seal(purpose, payload!, tenantId, toExecutionRtId);
    }

    private sealed class Wrapper
    {
        [JsonPropertyName("v")] public int Version { get; set; }
        [JsonPropertyName("p")] public string? Purpose { get; set; }
        [JsonPropertyName("t")] public string? TenantId { get; set; }
        [JsonPropertyName("e")] public string? ExecutionRtId { get; set; }
        [JsonPropertyName("d")] public JsonElement Data { get; set; }
    }
}
