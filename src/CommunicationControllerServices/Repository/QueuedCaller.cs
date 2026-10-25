using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;

/// <summary>
///     The invoker of a queued work item (AB#5279) as it is persisted on the <see cref="RtPipelineExecution" />
///     and read back for the lease.
/// </summary>
/// <remarks>
///     <para>
///         🔴 <b>AB#7472 — the stored caller is sealed.</b> The seven plain <c>Caller*</c> attributes are ordinary
///         writable attributes of the entity; whoever can edit a queued execution through a generic write surface
///         could change who a leased run executes as. They are no longer written and never read. The principal, its
///         trust level and the delegation token are written as ONE <see cref="IExecutionEnvelope" /> into
///         <c>SealedCaller</c>, bound to the tenant and to the RtId of this execution, and opened at lease grant. A
///         value that was edited, copied from another execution or written in plain text does not open: the run
///         proceeds without a caller, and the pipeline's <c>CallerBinding</c> decides (fail closed).
///     </para>
///     <para>
///         The trust level inside the envelope is the one the controller wrote; no stored trust level is trusted on
///         its own.
///     </para>
/// </remarks>
internal static class QueuedCaller
{
    /// <summary>The authenticated content of the envelope.</summary>
    internal sealed class Payload
    {
        public string? SubjectId { get; set; }
        public string? TenantId { get; set; }
        public string? Email { get; set; }
        public string? Name { get; set; }
        public List<string> Roles { get; set; } = [];
        public int TrustLevel { get; set; }

        /// <summary>Plain bearer token for delegation; protected by the envelope, removed when the lease is granted.</summary>
        public string? AccessToken { get; set; }
    }

    /// <summary>The verified content of a sealed caller.</summary>
    internal sealed record Opened(ExecutePipelineCaller Caller, string? AccessToken);

    /// <summary>
    ///     Seals the invoker onto the entity. A null caller writes nothing — the attribute stays unset, which is what
    ///     "queued without an invoker" looks like on read. When the controller cannot seal (no key ring) nothing is
    ///     written either: the run then proceeds without a caller.
    /// </summary>
    public static void Apply(RtPipelineExecution execution, string tenantId, ExecutePipelineCaller? caller,
        string? accessToken, IExecutionEnvelope envelope)
    {
        if (caller is null || string.IsNullOrEmpty(caller.SubjectId))
        {
            return;
        }

        execution.SealedCaller = envelope.Seal(ExecutionEnvelopePurposes.QueuedCaller, ToPayload(caller, accessToken),
            tenantId, execution.RtId);
    }

    /// <summary>
    ///     Opens the sealed caller of the execution, or returns null (and logs the reason without the value) when
    ///     there is none or it does not verify.
    /// </summary>
    public static Opened? Open(string? sealedCaller, string tenantId, OctoObjectId executionRtId,
        IExecutionEnvelope envelope)
    {
        if (!envelope.TryOpen<Payload>(ExecutionEnvelopePurposes.QueuedCaller, sealedCaller, tenantId, executionRtId,
                out var payload, out var rejection))
        {
            if (rejection != ExecutionEnvelopeRejection.Missing)
            {
                NLog.LogManager.GetCurrentClassLogger().Warn(
                    "[{TenantId}] The queued caller of execution '{ExecutionRtId}' did not verify ({Reason}); the run " +
                    "proceeds without a caller",
                    tenantId, executionRtId, rejection);
            }

            return null;
        }

        // The subject is the one field without which a caller is not a caller.
        if (payload is null || string.IsNullOrEmpty(payload.SubjectId))
        {
            return null;
        }

        return new Opened(new ExecutePipelineCaller
        {
            SubjectId = payload.SubjectId,
            TenantId = payload.TenantId,
            Email = payload.Email,
            Name = payload.Name,
            Roles = payload.Roles.ToArray(),
            TrustLevel = payload.TrustLevel
        }, string.IsNullOrEmpty(payload.AccessToken) ? null : payload.AccessToken);
    }

    /// <summary>
    ///     Seals the same caller again for another execution (the re-queued retry). The old value must verify for the
    ///     OLD execution; the ciphertext is never copied.
    /// </summary>
    public static string? Reseal(string? sealedCaller, string tenantId, OctoObjectId fromExecutionRtId,
        OctoObjectId toExecutionRtId, IExecutionEnvelope envelope) =>
        envelope.Reseal<Payload>(ExecutionEnvelopePurposes.QueuedCaller, sealedCaller, tenantId, fromExecutionRtId,
            toExecutionRtId);

    /// <summary>
    ///     The same envelope without the delegation token, sealed for the same execution. Written when the lease is
    ///     granted: the token is only needed until then, and a retry carries the caller without it. Returns the empty
    ///     string when there is nothing valid to keep (clears the attribute).
    /// </summary>
    public static string StripToken(string? sealedCaller, string tenantId, OctoObjectId executionRtId,
        IExecutionEnvelope envelope)
    {
        if (string.IsNullOrEmpty(sealedCaller))
        {
            return string.Empty;
        }

        if (!envelope.TryOpen<Payload>(ExecutionEnvelopePurposes.QueuedCaller, sealedCaller, tenantId, executionRtId,
                out var payload, out _) || payload is null)
        {
            return string.Empty;
        }

        payload.AccessToken = null;
        return envelope.Seal(ExecutionEnvelopePurposes.QueuedCaller, payload, tenantId, executionRtId) ?? string.Empty;
    }

    private static Payload ToPayload(ExecutePipelineCaller caller, string? accessToken) => new()
    {
        SubjectId = caller.SubjectId,
        TenantId = caller.TenantId,
        Email = caller.Email,
        Name = caller.Name,
        Roles = caller.Roles.ToList(),
        TrustLevel = caller.TrustLevel,
        AccessToken = string.IsNullOrEmpty(accessToken) ? null : accessToken
    };
}
