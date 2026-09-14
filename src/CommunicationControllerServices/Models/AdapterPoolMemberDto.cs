namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Models;

/// <summary>
///     One connected member of an adapter pool, as seen by this controller instance (AB#4924).
/// </summary>
/// <remarks>
///     ⚠️ <b>Per instance, not per cluster.</b> A member's SignalR connection lives on exactly one
///     controller pod, so this lists what <i>this</i> pod can reach. With more than one replica the
///     answer is partial by construction — the same property <c>IOperatorConnectionManager</c> has,
///     and it is stated here rather than discovered during an incident.
/// </remarks>
public sealed record AdapterPoolMemberDto
{
    /// <summary>Stable identity of the member process, as it proposed at registration.</summary>
    public required string MemberId { get; init; }

    /// <summary>The lease it currently holds, or null when it is idle.</summary>
    public string? ActiveLeaseId { get; init; }

    /// <summary>The tenant that lease serves, or null when the member is idle.</summary>
    public string? ActiveLeaseTenantId { get; init; }

    /// <summary>Whether the member was told to drain and will take no further lease.</summary>
    public required bool IsDraining { get; init; }

    /// <summary>When the member last registered or sent a heartbeat (UTC).</summary>
    public required DateTime LastSeenUtc { get; init; }
}
