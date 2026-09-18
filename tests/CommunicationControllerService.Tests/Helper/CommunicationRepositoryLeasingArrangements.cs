using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;

/// <summary>
///     Arranges the <c>LentFrom</c> resolution of a borrowing adapter on a substituted
///     <see cref="ICommunicationRepository" /> (AB#5271).
/// </summary>
/// <remarks>
///     Before AB#5271 a test declared a borrower by assigning two attributes on the
///     <see cref="RtAdapter" />. The lender now hangs off an association to a borrower-local
///     <see cref="RtLentAdapterPool" />, so it has to be arranged on the repository instead. One
///     helper rather than a copy in each test base: the two read shapes — single and batched — must
///     stay consistent, and a suite arranging only one of them fails in a way that reads like a
///     scheduler bug.
/// </remarks>
internal static class CommunicationRepositoryLeasingArrangements
{
    /// <summary>
    ///     Declares that <paramref name="adapter" /> borrows from
    ///     <paramref name="lenderAdapterPoolRtId" /> in <paramref name="lenderTenantId" />.
    /// </summary>
    public static RtLentAdapterPool ArrangeLentFrom(this ICommunicationRepository repository,
        string borrowerTenantId, RtAdapter adapter, string lenderTenantId, string lenderAdapterPoolRtId)
    {
        var mirror = RtEntityCreator.CreateLentAdapterPool(lenderTenantId, lenderAdapterPoolRtId);
        repository.ArrangeLentFrom(borrowerTenantId, adapter, mirror);
        return mirror;
    }

    /// <summary>
    ///     The same, for a mirror the test built itself (a half-written <c>Lender</c> record, say).
    /// </summary>
    public static void ArrangeLentFrom(this ICommunicationRepository repository, string borrowerTenantId,
        RtAdapter adapter, RtLentAdapterPool mirror)
    {
        repository.GetLentAdapterPoolForAdapterAsync(borrowerTenantId, adapter.RtId).Returns(mirror);
        repository
            .GetLentAdapterPoolsForAdaptersAsync(borrowerTenantId,
                Arg.Is<IReadOnlyCollection<OctoObjectId>>(ids => ids.Contains(adapter.RtId)))
            .Returns(_ => new Dictionary<OctoObjectId, RtLentAdapterPool> { [adapter.RtId] = mirror });
    }
}
