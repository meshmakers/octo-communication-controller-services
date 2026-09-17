using System.ComponentModel.DataAnnotations;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Models;

/// <summary>
///     Body of <c>POST {tenantId}/v1/adapterPool/{adapterPoolRtId}/lease</c> (AB#4924, increment 6).
/// </summary>
/// <remarks>
///     🔴 <b>This endpoint exists so a lease can be granted by hand.</b> It is not the production
///     path: the scheduler of increment 7 decides which borrower goes next, from a queue this
///     increment deliberately does not build. What the endpoint buys is that every mechanism below it
///     — the borrower declaration check, the lending scope, the credential, the hub routing, the
///     release — becomes exercisable end to end a whole increment before the scheduler exists.
/// </remarks>
/// <param name="BorrowerTenantId">The tenant whose work should run.</param>
/// <param name="AdapterRtId">
///     RtId of that tenant's <c>Adapter</c> whose <c>LifecycleMode</c> is <c>Leased</c>. The lease is
///     refused unless that adapter's own <c>LentFromTenantId</c> / <c>LentFromAdapterPoolRtId</c> name this
///     pool — lending is consensual in both directions.
/// </param>
/// <param name="ExecutionId">Optional pipeline execution the lease serves.</param>
/// <param name="TtlSeconds">
///     Optional lease lifetime. Absent takes the service default; the value is bounded on the way in
///     so a typo cannot pin a member for a day.
/// </param>
public sealed record GrantAdapterPoolLeaseRequestDto(
    [Required] string BorrowerTenantId,
    [Required] string AdapterRtId,
    string? ExecutionId = null,
    [Range(1, 3600)] int? TtlSeconds = null);
