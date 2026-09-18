using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.
    AdapterPoolMirrorProvisioningServiceTests;

/// <summary>
///     Shared arrangement for the AB#5271 mirror sync: one borrower, one candidate lender, and the
///     mirrors the borrower currently stores.
/// </summary>
internal abstract class AdapterPoolMirrorProvisioningServiceTestsBase
{
    protected const string BorrowerTenantId = "borrower";
    protected const string LenderTenantId = "lender";
    protected const string OtherLenderTenantId = "other-lender";

    protected readonly ICommunicationRepository CommunicationRepository =
        Substitute.For<ICommunicationRepository>();

    protected readonly ITenantLendingScopeResolver LendingScopeResolver =
        Substitute.For<ITenantLendingScopeResolver>();

    protected readonly AdapterPoolMirrorProvisioningService Service;

    /// <summary>What the borrower currently stores; mutated by the arrangements below.</summary>
    private readonly List<RtLentAdapterPool> _existingMirrors = [];

    protected AdapterPoolMirrorProvisioningServiceTestsBase()
    {
        Service = new AdapterPoolMirrorProvisioningService(
            NullLogger<AdapterPoolMirrorProvisioningService>.Instance,
            CommunicationRepository,
            LendingScopeResolver);

        CommunicationRepository.GetLentAdapterPoolMirrorsAsync(BorrowerTenantId)
            .Returns(_ => (IReadOnlyCollection<RtLentAdapterPool>)_existingMirrors.ToList());

        // Default: nothing lends anything anywhere. Each test declares only what it needs.
        ArrangeCandidateLenders();
        CommunicationRepository.GetAdapterPoolsForMirroringAsync(Arg.Any<string>())
            .Returns([]);
        LendingScopeResolver
            .MayLendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<LendingScope>(),
                Arg.Any<CancellationToken>())
            .Returns(false);
    }

    protected void ArrangeCandidateLenders(params string[] lenderTenantIds)
    {
        LendingScopeResolver.ResolveCandidateLenderTenantsAsync(BorrowerTenantId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyCollection<string>)lenderTenantIds.ToList());
    }

    /// <summary>
    ///     Declares one pool in <paramref name="lenderTenantId" /> and whether it lends to the
    ///     borrower.
    /// </summary>
    protected LendableAdapterPool ArrangePool(string lenderTenantId, string adapterPoolRtId, bool lends,
        string name = "a-pool", int sharingMode = LendingScope.Descendants)
    {
        var pool = new LendableAdapterPool(lenderTenantId, adapterPoolRtId, name, null,
            new LendingScope(sharingMode, null), 1, 3, 0);

        var existing = CommunicationRepository.GetAdapterPoolsForMirroringAsync(lenderTenantId)
            .Result.ToList();
        existing.Add(pool);
        CommunicationRepository.GetAdapterPoolsForMirroringAsync(lenderTenantId)
            .Returns((IReadOnlyCollection<LendableAdapterPool>)existing);

        LendingScopeResolver
            .MayLendAsync(lenderTenantId, BorrowerTenantId, pool.Scope, Arg.Any<CancellationToken>())
            .Returns(lends);

        return pool;
    }

    /// <summary>Declares a mirror the borrower already stores.</summary>
    protected RtLentAdapterPool ArrangeExistingMirror(string lenderTenantId, string adapterPoolRtId)
    {
        var mirror = RtEntityCreator.CreateLentAdapterPool(lenderTenantId, adapterPoolRtId);
        _existingMirrors.Add(mirror);
        return mirror;
    }

    /// <summary>Declares a stored mirror whose <c>Lender</c> record says nothing usable.</summary>
    protected RtLentAdapterPool ArrangeBrokenExistingMirror()
    {
        var mirror = RtEntityCreator.CreateLentAdapterPool(LenderTenantId, "irrelevant");
        // The controller writes the record as one unit, so this shape only arises from a hand edit
        // in the borrower's own database — which is exactly why it has to be handled.
        mirror.Lender = new RtLenderReferenceRecord
        {
            LenderTenantId = string.Empty,
            LenderAdapterPoolRtId = string.Empty
        };
        _existingMirrors.Add(mirror);
        return mirror;
    }

    protected static OctoObjectId NewPoolRtId()
    {
        return OctoObjectId.GenerateNewId();
    }
}
