namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Models;

/// <summary>
///     One entry of an adapter pool's queue, as all three surfaces read it (AB#4924 §10).
/// </summary>
/// <remarks>
///     <para>
///         🔴 <b>A GraphQL query cannot serve this.</b> Two of its fields —
///         <see cref="PositionInTenant" /> and <see cref="TenantsAheadInRotation" /> — are the
///         scheduler's rotation cursor, which is process state, not entity state. And the entries
///         span tenant databases: the pool belongs to the lender, every execution to a borrower.
///     </para>
///     <para>
///         🔴 <b>There is no global rank field, and adding one would be a lie.</b> Round-robin serves
///         tenants in turns, so the only true answer is the pair below: where this item sits inside
///         its own tenant's queue, and how many other tenants take a turn first.
///     </para>
/// </remarks>
public class AdapterPoolQueueEntryDto
{
    /// <summary>The borrower's execution id.</summary>
    public required string ExecutionId { get; set; }

    /// <summary>The tenant whose work this is.</summary>
    public required string BorrowerTenantId { get; set; }

    /// <summary>RtId of the pipeline to run, when the association resolves.</summary>
    public string? PipelineRtId { get; set; }

    /// <summary>Display name of that pipeline.</summary>
    public string? PipelineName { get; set; }

    /// <summary>
    ///     <c>0</c> Interactive, <c>1</c> Batch. Interactive is served first <b>within this tenant's
    ///     turn</b> and has no effect at all across tenants.
    /// </summary>
    public int ExecutionClass { get; set; }

    /// <summary>When the work item entered the queue.</summary>
    public DateTime QueuedAtUtc { get; set; }

    /// <summary>1-based position inside this tenant's own queue; <c>0</c> once the item is leased.</summary>
    public int PositionInTenant { get; set; }

    /// <summary>
    ///     How many other borrowing tenants with queued work are served before this tenant's next
    ///     turn; <c>0</c> once the item is leased.
    /// </summary>
    public int TenantsAheadInRotation { get; set; }

    /// <summary>
    ///     The pool member holding this item, or null while it waits. Nothing enforces referential
    ///     integrity on this value — it may name a process that no longer exists (implementation plan
    ///     §12.1) — so render it, never resolve it.
    /// </summary>
    public string? LeasedOnMemberId { get; set; }

    /// <summary>When the holding lease expires; null while the item waits.</summary>
    public DateTime? LeaseExpiresAtUtc { get; set; }
}
