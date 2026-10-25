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

        /// <summary>SHA-256 of the input the controller queued (AB#7473), so the input cannot be swapped afterwards.</summary>
        public string? InputDataHash { get; set; }

        /// <summary>Plain bearer token for delegation; protected by the envelope, removed when the lease is granted.</summary>
        public string? AccessToken { get; set; }
    }

    private const int ProvenanceVersion = 1;

    /// <summary>
    ///     The verified content of a sealed provenance value. <see cref="Caller" /> is null for an execution that was
    ///     queued by the controller without an invoker (cron, internal).
    /// </summary>
    internal sealed record Opened(ExecutePipelineCaller? Caller, string? AccessToken);

    /// <summary>
    ///     Seals the provenance of the execution (and the invoker, when there is one) onto the entity. Always writes,
    ///     also for a null caller. When the controller cannot seal (no key ring) nothing is written and the execution
    ///     is later refused at lease grant (fail closed); the returned flag says so.
    /// </summary>
    public static bool Apply(RtPipelineExecution execution, string tenantId, OctoObjectId pipelineRtId,
        ExecutePipelineCaller? caller, string? accessToken, IExecutionEnvelope envelope)
    {
        var payload = ToPayload(caller, accessToken);
        payload.Provenance = ProvenanceVersion;
        payload.PipelineRtId = pipelineRtId.ToString();
        payload.InputDataHash = HashInput(execution.InputData);

        execution.SealedCaller = envelope.Seal(ExecutionEnvelopePurposes.QueuedCaller, payload, tenantId,
            execution.RtId);
        return execution.SealedCaller is not null;
    }

    /// <summary>
    ///     Opens the provenance of a queued execution. <c>false</c> (with a reason that carries no value) means the
    ///     execution was not queued by this controller for this tenant, execution, pipeline and input.
    /// </summary>
    public static bool TryOpen(string? sealedCaller, string tenantId, OctoObjectId executionRtId,
        OctoObjectId? pipelineRtId, string? inputData, IExecutionEnvelope envelope, out Opened? opened,
        out string reason)
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
            return false;
        }

        if (payload.Provenance != ProvenanceVersion)
        {
            reason = "provenance marker missing (queued before the upgrade)";
            return false;
        }

        if (pipelineRtId is null || !string.Equals(payload.PipelineRtId, pipelineRtId.ToString(),
                StringComparison.Ordinal))
        {
            reason = "pipeline differs from the one that was queued";
            return false;
        }

        if (!string.Equals(payload.InputDataHash, HashInput(inputData), StringComparison.Ordinal))
        {
            reason = "input differs from the one that was queued";
            return false;
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

        opened = new Opened(caller, string.IsNullOrEmpty(payload.AccessToken) ? null : payload.AccessToken);
        reason = string.Empty;
        return true;
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
            }, string.IsNullOrEmpty(payload.AccessToken) ? null : payload.AccessToken);
    }

    private static string HashInput(string? input) =>
        Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(input ?? string.Empty)));

    /// <summary>
    ///     Seals the same caller again for another execution (the re-queued retry). The old value must verify for the
    ///     OLD execution; the ciphertext is never copied.
    /// </summary>
    public static string? Reseal(string? sealedCaller, string tenantId, OctoObjectId fromExecutionRtId,
        OctoObjectId toExecutionRtId, IExecutionEnvelope envelope)
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
