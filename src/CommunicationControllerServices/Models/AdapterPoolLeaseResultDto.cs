namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Models;

/// <summary>
///     Result of a hand-granted lease (AB#4924, increment 6).
/// </summary>
/// <remarks>
///     🔴 Carries no credential. The borrower's client secret travels on the lease to the member and
///     nowhere else — returning it here would put it in a REST response, a proxy log and whatever
///     shell history the operator used to call the endpoint.
/// </remarks>
public sealed record AdapterPoolLeaseResultDto
{
    /// <summary>Whether a member is now holding the lease.</summary>
    public required bool Granted { get; init; }

    /// <summary>Identifier of the granted lease, or null.</summary>
    public string? LeaseId { get; init; }

    /// <summary>
    ///     The member that took it — the value the borrower's execution records as
    ///     <c>LeasedOnMemberId</c>. Null when nothing was granted.
    /// </summary>
    public string? MemberId { get; init; }

    /// <summary>Why nothing was granted. Null on success.</summary>
    public string? StatusMessage { get; init; }
}
