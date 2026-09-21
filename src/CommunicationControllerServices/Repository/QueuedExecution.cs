using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;

/// <summary>
///     One work item waiting for a lease (AB#4924, increment 7).
/// </summary>
/// <remarks>
///     <para>
///         Flattened off <c>RtPipelineExecution</c> plus its <c>ExecutedPipeline</c> edge, because the
///         scheduler needs the pipeline's <c>ExecutionClass</c> on every ordering decision and the
///         three surfaces (§10) need the pipeline's name. Resolving both per dequeue would put an
///         association walk on the hot path of every scheduling round.
///     </para>
///     <para>
///         🔴 <b>Not a queue entity.</b> Concept §5 keeps one work item as one
///         <c>PipelineExecution</c> from enqueue to completion; this record is a read projection of
///         that entity, never a second source of truth.
///     </para>
/// </remarks>
/// <param name="ExecutionId">The execution's business id (a GUID string), as every surface names it.</param>
/// <param name="ExecutionRtId">Runtime id of the <c>PipelineExecution</c> entity.</param>
/// <param name="QueuedAtUtc">When the work item entered the queue — the ordering key within one tenant's class.</param>
/// <param name="PipelineRtId">The pipeline to run, or null when the edge cannot be resolved.</param>
/// <param name="PipelineName">Display name of that pipeline, for the surfaces.</param>
/// <param name="ExecutionClass">
///     <c>0</c> Interactive, <c>1</c> Batch — the numeric keys of the <c>PipelineExecutionClass</c> CK
///     enum. Ordered ascending, so Interactive sorts ahead of Batch without a mapping table.
/// </param>
/// <param name="InputData">
///     The pipeline input the trigger supplied, stored on the execution at enqueue. Carried here
///     because the lease has to carry the work (AB#4924 §9.9 / D4): a member that received only an
///     execution id had nothing to run. Read from the entity rather than re-derived, so the retry of
///     an interrupted attempt and its original run the same input.
/// </param>
/// <param name="Caller">
///     The invoker the work item was queued for (AB#5279) — token-free principal, read from the
///     entity like the input. Null when queued without one (a cron tick, an internal caller).
/// </param>
/// <param name="CallerAccessToken">
///     The invoker's token exactly as stored on the entity — encrypted, or null. Decrypting and
///     judging its expiry is the lease service's job at grant time.
/// </param>
public sealed record QueuedExecution(
    string ExecutionId,
    OctoObjectId ExecutionRtId,
    DateTime QueuedAtUtc,
    OctoObjectId? PipelineRtId,
    string? PipelineName,
    int ExecutionClass,
    string? InputData = null,
    ExecutePipelineCaller? Caller = null,
    string? CallerAccessToken = null)
{
    /// <summary><c>PipelineExecutionClass.Interactive</c>: work a human is waiting for.</summary>
    public const int InteractiveClass = 0;

    /// <summary><c>PipelineExecutionClass.Batch</c>: everything scheduled or event-driven.</summary>
    public const int BatchClass = 1;

    /// <summary>
    ///     The order work items of <b>one</b> tenant are served in (AB#4924 §9.2).
    /// </summary>
    /// <remarks>
    ///     🔴 <b>Within a tenant's turn only.</b> Class first — Interactive (0) before Batch (1) —
    ///     then arrival, then the execution id so two items enqueued in the same millisecond still
    ///     have a stable order. It must never be applied ACROSS tenants: that would let a tenant with
    ///     an Interactive job overtake a whole rotation and reintroduce the starvation round-robin
    ///     exists to prevent.
    /// </remarks>
    public static readonly IComparer<QueuedExecution> SchedulingOrder =
        Comparer<QueuedExecution>.Create((a, b) =>
        {
            var byClass = a.ExecutionClass.CompareTo(b.ExecutionClass);
            if (byClass != 0)
            {
                return byClass;
            }

            var byArrival = a.QueuedAtUtc.CompareTo(b.QueuedAtUtc);
            return byArrival != 0
                ? byArrival
                : string.CompareOrdinal(a.ExecutionId, b.ExecutionId);
        });
}

/// <summary>
///     What the scheduler stamps on an execution when it takes it out of the queue (AB#4924 §9.1).
/// </summary>
/// <param name="LeaseId">The lease the work item is served under.</param>
/// <param name="LenderTenantId">Tenant that owns the pool — becomes <c>LeasedFromTenantId</c>.</param>
/// <param name="AdapterPoolRtId">RtId of the <c>AdapterPool</c> — becomes <c>LeasedFromAdapterPoolRtId</c>.</param>
/// <param name="MemberId">
///     The member that holds the lease — becomes <c>LeasedOnMemberId</c>. Not an RtId: members are
///     replicas of one pool workload rather than entities (implementation plan §13.1).
/// </param>
/// <param name="GrantedAtUtc">
///     When the lease was granted. Becomes both <c>LeaseGrantedAt</c> and <c>StartedAt</c>, and with
///     <c>QueuedAt</c> it yields <c>LeaseWaitMs</c>.
/// </param>
public readonly record struct LeaseClaim(
    string LeaseId,
    string LenderTenantId,
    string AdapterPoolRtId,
    string MemberId,
    DateTime GrantedAtUtc);

/// <summary>
///     What an interrupted leased attempt was for, so a fresh attempt can be enqueued in its place
///     (AB#4924 §9.3).
/// </summary>
/// <remarks>
///     🔴 <b>A re-queue is a NEW execution entity, not the old one moved back to <c>Queued</c>.</b>
///     Concept §6 requires two things at once — "the controller re-queues the execution" <i>and</i>
///     "marks the previous attempt <c>Interrupted</c>" — and one entity cannot hold both states. The
///     interrupted attempt keeps its own <c>LeaseGrantedAt</c>/<c>LeaseReleasedAt</c> span, which is
///     what makes the billing input (concept §4b) honest about time a member really was held; the
///     retry gets its own <c>QueuedAt</c> and therefore its own honest <c>LeaseWaitMs</c>. Reusing
///     the entity would erase both.
/// </remarks>
/// <param name="PipelineRtEntityId">The pipeline the attempt was running.</param>
/// <param name="AdapterRtEntityId">The borrower's Leased adapter that owns the work.</param>
/// <param name="TriggerType">Trigger type of the original attempt, carried onto the retry.</param>
/// <param name="InputData">Pipeline input of the original attempt, carried onto the retry.</param>
/// <param name="Caller">Invoker of the original attempt, carried onto the retry (AB#5279).</param>
/// <param name="CallerAccessToken">The invoker's token as stored (encrypted), carried onto the retry as-is.</param>
public sealed record InterruptedLeasedExecution(
    RtEntityId PipelineRtEntityId,
    RtEntityId AdapterRtEntityId,
    RtPipelineTriggerTypeEnum TriggerType,
    string? InputData,
    ExecutePipelineCaller? Caller = null,
    string? CallerAccessToken = null);
