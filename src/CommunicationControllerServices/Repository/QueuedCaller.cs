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
///         🔴 <b>AB#7473 — the envelope is also the provenance of the work item.</b> The controller seals it for EVERY
///         execution it queues, also when there is no caller (cron, internal): a missing or invalid envelope means
///         the execution was not queued by the controller (it was written through a generic surface) and it is never
///         leased. The pipeline and the input are part of the authenticated content, so a controller-queued
///         execution whose pipeline association or input was edited afterwards does not open either. The caller is
///         the optional part of the same value.
///     </para>
///     <para>
///         🔴 <b>AB#7472 — the stored caller is sealed.</b> The seven plain <c>Caller*</c> attributes are ordinary
///         writable attributes of the entity; whoever can edit a queued execution through a generic write surface
///         could change who a leased run executes as. They are no longer written and never read. The principal, its
///         trust level and the delegation token are written as ONE <see cref="IExecutionEnvelope" /> into
///         <c>SealedCaller</c>, bound to the tenant and to the RtId of this execution, and opened at lease grant. A
///         value that was edited, copied from another execution or written in plain text does not open: the
///         execution is not leased (AB#7473, failed with a reason).
///     </para>
///     <para>
///         The trust level inside the envelope is the one the controller wrote; no stored trust level is trusted on
///         its own.
///     </para>
///     <para>
///         🔴 <b>AB#7461 — the method-invocation marker rides in the same envelope.</b> A run started by the
///         invocation of a CK method (<c>PipelineMethodInvoker</c>) is queued with the marker
///         (<see cref="ExecutePipelineMethodInvocation" />) as one more member of the sealed payload. It is therefore
///         bound to the tenant and the execution RtId like the caller, re-sealed for the retry's RtId on requeue and
///         never writable through a generic surface; no second attribute (and no System.Communication bump) is needed.
///         It is projected into <c>LeaseDto.MethodInvocation</c> only when the envelope as a whole opens AND the
///         marker's <c>PipelineRtId</c> equals the pipeline the controller queued; otherwise the marker is dropped
///         (WARN without values) and the adapter-side guard refuses. REST and cron never pass a marker, so their
///         payload has none.
///     </para>
///     <para>
///         🔴 <b>AB#7476 — the envelope is single use.</b> A seal is leased at most once. Its single-use identity
///         (<see cref="Payload.ClaimId" />, a random value) and the time it was sealed
///         (<see cref="Payload.IssuedAtUtc" />) are part of the authenticated payload; the lease service records the
///         identity in a controller-owned claim record (<see cref="IExecutionLeaseClaimStore" />) before the lease is
///         handed out. A re-queue seals AGAIN with a new identity. A value without an identity (queued before the
///         upgrade) is refused (fail closed, no migration: accepting it would be an unclaimable seal).
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

        /// <summary>Provenance marker (AB#7473). Absent in a value sealed before the marker existed, which is refused.</summary>
        public int Provenance { get; set; }

        /// <summary>The pipeline the controller queued the execution for (AB#7473).</summary>
        public string? PipelineRtId { get; set; }

        /// <summary>The adapter the controller queued the execution for (AB#7473).</summary>
        public string? AdapterRtId { get; set; }

        /// <summary>SHA-256 of the input the controller queued (AB#7473), so the input cannot be swapped afterwards.</summary>
        public string? InputDataHash { get; set; }

        /// <summary>Plain bearer token for delegation; protected by the envelope, removed when the lease is granted.</summary>
        public string? AccessToken { get; set; }

        /// <summary>
        ///     The method-invocation marker (AB#7461). Null for every run that was not started by a CK method
        ///     invocation, and in a value sealed before the marker existed.
        /// </summary>
        public ExecutePipelineMethodInvocation? MethodInvocation { get; set; }

        /// <summary>
        ///     The single-use identity of this seal (AB#7476): random, fresh for every seal (enqueue and re-queue).
        ///     Absent in a value sealed before the identity existed, which is refused.
        /// </summary>
        public string? ClaimId { get; set; }

        /// <summary>
        ///     When the controller sealed the value (AB#7476). Kept across the token strip, renewed by a re-seal. A seal
        ///     older than the accepted age is refused, which is what lets the claim records expire.
        /// </summary>
        public DateTime? IssuedAtUtc { get; set; }
    }

    private const int ProvenanceVersion = 1;

    /// <summary>
    ///     The verified content of a sealed provenance value. <see cref="Caller" /> is null for an execution that was
    ///     queued by the controller without an invoker (cron, internal).
    /// </summary>
    /// <remarks>
    ///     <see cref="MethodInvocation" /> (AB#7461) is the marker as it was queued, only when it is consistent with the
    ///     pipeline the controller queued; otherwise null.
    /// </remarks>
    internal sealed record Opened(
        ExecutePipelineCaller? Caller,
        string? AccessToken,
        ExecutePipelineMethodInvocation? MethodInvocation = null,
        string? ClaimId = null,
        DateTime? IssuedAtUtc = null);

    /// <summary>
    ///     Seals the provenance of the execution (and the invoker, when there is one) onto the entity. Always writes,
    ///     also for a null caller. When the controller cannot seal (no key ring) nothing is written and the execution
    ///     is later refused at lease grant (fail closed); the returned flag says so.
    /// </summary>
    public static bool Apply(RtPipelineExecution execution, string tenantId, OctoObjectId pipelineRtId,
        OctoObjectId adapterRtId, ExecutePipelineCaller? caller, string? accessToken, IExecutionEnvelope envelope,
        ExecutePipelineMethodInvocation? methodInvocation = null, DateTime? nowUtc = null)
    {
        var payload = ToPayload(caller, accessToken);
        payload.ClaimId = NewClaimId();
        payload.IssuedAtUtc = nowUtc ?? DateTime.UtcNow;
        payload.MethodInvocation = methodInvocation;
        payload.Provenance = ProvenanceVersion;
        payload.PipelineRtId = pipelineRtId.ToString();
        payload.AdapterRtId = adapterRtId.ToString();
        payload.InputDataHash = HashInput(execution.InputData);

        execution.SealedCaller = envelope.Seal(ExecutionEnvelopePurposes.QueuedCaller, payload, tenantId,
            execution.RtId);
        return execution.SealedCaller is not null;
    }

    /// <summary>The verdict on the provenance of a queued execution.</summary>
    internal enum Provenance
    {
        /// <summary>The controller queued this execution for this tenant, pipeline, adapter and input.</summary>
        Valid,

        /// <summary>
        ///     Not provable HERE: the controller has no key (ring) for the value. The execution may well be genuine, so it
        ///     is neither leased nor failed; it stays queued.
        /// </summary>
        Unverifiable,

        /// <summary>Missing, edited, copied or otherwise not the controller's own value. Terminal.</summary>
        Forged
    }

    /// <summary>
    ///     Opens the provenance of a queued execution. The reason carries no value.
    /// </summary>
    public static Provenance TryOpen(string? sealedCaller, string tenantId, OctoObjectId executionRtId,
        OctoObjectId? pipelineRtId, OctoObjectId? adapterRtId, string? inputData, IExecutionEnvelope envelope,
        out Opened? opened, out string reason)
    {
        opened = null;
        if (!envelope.TryOpen<Payload>(ExecutionEnvelopePurposes.QueuedCaller, sealedCaller, tenantId, executionRtId,
                out var payload, out var rejection) || payload is null)
        {
            reason = rejection switch
            {
                ExecutionEnvelopeRejection.Missing => "no provenance",
                _ => $"provenance rejected ({rejection})"
            };
            return rejection is ExecutionEnvelopeRejection.NoKey or ExecutionEnvelopeRejection.UnknownKey
                ? Provenance.Unverifiable
                : Provenance.Forged;
        }

        if (payload.Provenance != ProvenanceVersion)
        {
            reason = "provenance marker missing (queued before the upgrade)";
            return Provenance.Forged;
        }

        if (pipelineRtId is null || !string.Equals(payload.PipelineRtId, pipelineRtId.ToString(),
                StringComparison.Ordinal))
        {
            reason = "pipeline differs from the one that was queued";
            return Provenance.Forged;
        }

        if (adapterRtId is null || !string.Equals(payload.AdapterRtId, adapterRtId.ToString(), StringComparison.Ordinal))
        {
            reason = "adapter differs from the one that was queued";
            return Provenance.Forged;
        }

        if (!string.Equals(payload.InputDataHash, HashInput(inputData), StringComparison.Ordinal))
        {
            reason = "input differs from the one that was queued";
            return Provenance.Forged;
        }

        // AB#7476: a seal without a single-use identity cannot be claimed, so it is never leased (no migration).
        if (string.IsNullOrEmpty(payload.ClaimId) || payload.IssuedAtUtc is null)
        {
            reason = "no single-use identity (queued before the upgrade)";
            return Provenance.Forged;
        }

        // The subject is the one field without which a caller is not a caller.
        var caller = string.IsNullOrEmpty(payload.SubjectId)
            ? null
            : new ExecutePipelineCaller
            {
                SubjectId = payload.SubjectId,
                TenantId = payload.TenantId,
                Email = payload.Email,
                Name = payload.Name,
                Roles = payload.Roles.ToArray(),
                TrustLevel = payload.TrustLevel
            };

        opened = new Opened(caller, string.IsNullOrEmpty(payload.AccessToken) ? null : payload.AccessToken,
            ConsistentMethodInvocation(payload, tenantId, executionRtId), payload.ClaimId, payload.IssuedAtUtc);
        reason = string.Empty;
        return Provenance.Valid;
    }

    /// <summary>
    ///     Opens the envelope for its tenant and execution only, without the pipeline and input binding. For
    ///     assertions on a stored value and for the re-seal; the lease grant uses <see cref="TryOpen" />.
    /// </summary>
    internal static Opened? Open(string? sealedCaller, string tenantId, OctoObjectId executionRtId,
        IExecutionEnvelope envelope)
    {
        if (!envelope.TryOpen<Payload>(ExecutionEnvelopePurposes.QueuedCaller, sealedCaller, tenantId, executionRtId,
                out var payload, out _) || payload is null)
        {
            return null;
        }

        return new Opened(string.IsNullOrEmpty(payload.SubjectId)
            ? null
            : new ExecutePipelineCaller
            {
                SubjectId = payload.SubjectId,
                TenantId = payload.TenantId,
                Email = payload.Email,
                Name = payload.Name,
                Roles = payload.Roles.ToArray(),
                TrustLevel = payload.TrustLevel
            }, string.IsNullOrEmpty(payload.AccessToken) ? null : payload.AccessToken,
            ConsistentMethodInvocation(payload, tenantId, executionRtId), payload.ClaimId, payload.IssuedAtUtc);
    }

    /// <summary>
    ///     Whether the seal is older than <paramref name="maxAge" /> (AB#7476). A seal older than that is refused at
    ///     lease grant, which is the invariant that allows the claim records to be deleted after
    ///     <paramref name="maxAge" /> plus a margin.
    /// </summary>
    public static bool IsExpired(Opened opened, TimeSpan maxAge, DateTime nowUtc) =>
        opened.IssuedAtUtc is not { } issuedAt || nowUtc - issuedAt > maxAge;

    private static string NewClaimId() => Guid.NewGuid().ToString("N");

    /// <summary>
    ///     AB#7461 — the marker of an opened payload, or null. A marker is only believed while it names the pipeline the
    ///     controller queued (the same pipeline the lease runs): the adapter additionally checks it against the pipeline
    ///     it executes. A marker without a method id, or one that names another pipeline, is dropped (fail closed: the
    ///     adapter-side guard then refuses the insert). Logs execution and tenant only, never the marker.
    /// </summary>
    private static ExecutePipelineMethodInvocation? ConsistentMethodInvocation(Payload payload, string tenantId,
        OctoObjectId executionRtId)
    {
        var marker = payload.MethodInvocation;
        if (marker is null)
        {
            return null;
        }

        if (string.IsNullOrEmpty(marker.MethodId) || string.IsNullOrEmpty(marker.PipelineRtId) ||
            !string.Equals(marker.PipelineRtId, payload.PipelineRtId, StringComparison.Ordinal))
        {
            NLog.LogManager.GetCurrentClassLogger().Warn(
                "[{TenantId}] The method-invocation marker of execution '{ExecutionRtId}' is inconsistent with the " +
                "queued pipeline and is dropped; the run starts without it",
                tenantId, executionRtId);
            return null;
        }

        return marker;
    }

    private static string HashInput(string? input) =>
        Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(input ?? string.Empty)));

    /// <summary>
    ///     Seals the same caller again for another execution (the re-queued retry). The old value must verify for the
    ///     OLD execution; the ciphertext is never copied.
    /// </summary>
    public static string? Reseal(string? sealedCaller, string tenantId, OctoObjectId fromExecutionRtId,
        OctoObjectId toExecutionRtId, IExecutionEnvelope envelope, DateTime? nowUtc = null)
    {
        if (!envelope.TryOpen<Payload>(ExecutionEnvelopePurposes.QueuedCaller, sealedCaller, tenantId,
                fromExecutionRtId, out var payload, out var rejection) || payload is null)
        {
            if (rejection != ExecutionEnvelopeRejection.Missing)
            {
                NLog.LogManager.GetCurrentClassLogger().Warn(
                    "[{TenantId}] The queued caller of execution '{ExecutionRtId}' did not verify ({Reason}) and is " +
                    "not carried onto the re-queued execution",
                    tenantId, fromExecutionRtId, rejection);
            }

            return null;
        }

        // A retry never carries delegation, whatever value is stored (the pre-claim value could be written back).
        payload.AccessToken = null;

        // AB#7461: the marker is carried (same work item, same invocation) but sealed again for the retry's RtId;
        // an inconsistent one is dropped here already.
        payload.MethodInvocation = ConsistentMethodInvocation(payload, tenantId, fromExecutionRtId);

        // AB#7476: the retry is a new work item with a NEW single-use identity; the old one was (or will be) claimed
        // by the attempt that was interrupted and must not travel on.
        payload.ClaimId = NewClaimId();
        payload.IssuedAtUtc = nowUtc ?? DateTime.UtcNow;
        return envelope.Seal(ExecutionEnvelopePurposes.QueuedCaller, payload, tenantId, toExecutionRtId);
    }

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

    private static Payload ToPayload(ExecutePipelineCaller? caller, string? accessToken) => caller is null ||
                                                                                            string.IsNullOrEmpty(caller.SubjectId)
        ? new Payload()
        : new Payload
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
