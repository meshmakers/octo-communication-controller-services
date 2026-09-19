using System.Security.Claims;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Controllers;

/// <summary>
///     AB#5271 — <c>GET {borrowerTenantId}/v1/adapterPool/lent/{lenderTenantId}/{adapterPoolRtId}</c>,
///     the read-only detail view a BORROWING tenant gets of a pool lent to it.
/// </summary>
/// <remarks>
///     <para>
///         Two things are worth a test here and neither is the happy path's field mapping. The first
///         is that the endpoint refuses a pool that does not lend to the caller <b>with the same
///         404 as a pool that does not exist</b> — anything else turns it into a way to enumerate
///         another tenant's pools. The second is the queue split: the caller's own entries in full,
///         everyone else's as counts only. That split is the privacy boundary of the whole screen,
///         so it is pinned field by field rather than by "the list is shorter".
///     </para>
///     <para>
///         🔴 The mirror is deliberately absent from every test, because it is absent from the
///         endpoint: the pool is resolved from the route values and authorized against the LENDER's
///         own scope. A test that set up a mirror would suggest the mirror is consulted, which is
///         precisely the thing that must never become true.
///     </para>
/// </remarks>
internal class AdapterPoolControllerLentDetailsTests
{
    private const string BorrowerTenantId = "gastroacker";
    private const string LenderTenantId = "accounting";

    private static readonly OctoObjectId PoolRtId = OctoObjectId.GenerateNewId();

    private readonly IAdapterPoolConnectionManager _connectionManager =
        Substitute.For<IAdapterPoolConnectionManager>();

    private readonly ILeaseSchedulerService _leaseScheduler = Substitute.For<ILeaseSchedulerService>();
    private readonly ITenantLendingScopeResolver _lendingScopeResolver =
        Substitute.For<ITenantLendingScopeResolver>();

    private readonly ICommunicationRepository _repo = Substitute.For<ICommunicationRepository>();

    private static AdapterPoolDetails SampleDetails(int sharingMode = LendingScope.Descendants)
    {
        return new AdapterPoolDetails(
            LenderTenantId,
            PoolRtId.ToString(),
            "Shared adapters",
            "The accounting pool",
            new LendingScope(sharingMode, null),
            MaxConcurrentLeasesPerTenant: 2,
            MinReplicas: 1,
            MaxReplicas: 4,
            PoolMemberCpuRequest: "200m",
            PoolMemberCpuLimit: "1",
            PoolMemberMemoryRequest: "512Mi",
            PoolMemberMemoryLimit: "1Gi",
            ScaleUpPolicy: 0,
            ScaleUpQueueDepthThreshold: 5,
            ScaleUpQueueWaitSeconds: 60,
            ChartName: "octo-mesh-adapter",
            ChartVersion: "0.2.260917008",
            DeploymentState: 2,
            StatusMessage: null);
    }

    private AdapterPoolController CreateSut()
    {
        var controller = new AdapterPoolController(_connectionManager, _leaseScheduler,
            Substitute.For<ILeaseService>(), Substitute.For<IAdapterPoolMirrorProvisioningService>(),
            _repo, _lendingScopeResolver, NullLogger<AdapterPoolController>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = BorrowerTenantId;
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([], "TestAuth", "name", "role"));
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static PoolMemberConnection Member(string memberId, LeaseDto? lease = null, bool draining = false)
    {
        return new PoolMemberConnection($"conn-{memberId}", memberId, LenderTenantId, PoolRtId.ToString(),
            lease, draining, DateTime.UtcNow);
    }

    // ------------------------------------------------------------------ authorization

    [Test]
    public async Task GetLentPoolDetails_PoolThatDoesNotLendHere_Returns404()
    {
        var sut = CreateSut();
        _repo.TryGetAdapterPoolDetailsAsync(LenderTenantId, PoolRtId.ToString()).Returns(SampleDetails());
        _lendingScopeResolver
            .MayLendAsync(LenderTenantId, BorrowerTenantId, Arg.Any<LendingScope>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await sut.GetLentPoolDetailsAsync(LenderTenantId, PoolRtId.ToString());

        await Assert.That(result).IsTypeOf<NotFoundObjectResult>();
    }

    [Test]
    public async Task GetLentPoolDetails_PoolThatDoesNotLendHere_SaysTheSameThingAsAMissingPool()
    {
        // 🔴 The point of the test: a refused pool and an absent pool are indistinguishable to the
        // caller. If these two messages ever diverge, the endpoint has become a pool enumerator.
        var sut = CreateSut();

        _repo.TryGetAdapterPoolDetailsAsync(LenderTenantId, PoolRtId.ToString()).Returns(SampleDetails());
        _lendingScopeResolver
            .MayLendAsync(LenderTenantId, BorrowerTenantId, Arg.Any<LendingScope>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var refused = await sut.GetLentPoolDetailsAsync(LenderTenantId, PoolRtId.ToString());

        _repo.TryGetAdapterPoolDetailsAsync(LenderTenantId, PoolRtId.ToString())
            .Returns((AdapterPoolDetails?)null);
        var missing = await sut.GetLentPoolDetailsAsync(LenderTenantId, PoolRtId.ToString());

        var refusedMessage = ((refused as NotFoundObjectResult)!.Value as ErrorResponse)!.ErrorMessage;
        var missingMessage = ((missing as NotFoundObjectResult)!.Value as ErrorResponse)!.ErrorMessage;
        await Assert.That(refusedMessage).IsEqualTo(missingMessage);
    }

    [Test]
    public async Task GetLentPoolDetails_UnresolvablePool_Returns404WithoutAskingTheScopeResolver()
    {
        var sut = CreateSut();
        _repo.TryGetAdapterPoolDetailsAsync(LenderTenantId, PoolRtId.ToString())
            .Returns((AdapterPoolDetails?)null);

        var result = await sut.GetLentPoolDetailsAsync(LenderTenantId, PoolRtId.ToString());

        using var _ = Assert.Multiple();
        await Assert.That(result).IsTypeOf<NotFoundObjectResult>();
        await _lendingScopeResolver.DidNotReceive().MayLendAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<LendingScope>(), Arg.Any<CancellationToken>());
    }

    // ------------------------------------------------------------------ the pool's shape

    [Test]
    public async Task GetLentPoolDetails_LendingPool_CarriesTheSizingTheMirrorDoesNot()
    {
        var sut = CreateSut();
        _repo.TryGetAdapterPoolDetailsAsync(LenderTenantId, PoolRtId.ToString()).Returns(SampleDetails());
        _lendingScopeResolver
            .MayLendAsync(LenderTenantId, BorrowerTenantId, Arg.Any<LendingScope>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _connectionManager.GetMembers(LenderTenantId, PoolRtId.ToString()).Returns([]);
        _leaseScheduler.GetQueueAsync(LenderTenantId, Arg.Any<OctoObjectId>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var dto = ((await sut.GetLentPoolDetailsAsync(LenderTenantId, PoolRtId.ToString())) as OkObjectResult)!
            .Value as LentAdapterPoolDetailsDto;

        using var _ = Assert.Multiple();
        await Assert.That(dto!.ChartName).IsEqualTo("octo-mesh-adapter");
        await Assert.That(dto.ChartVersion).IsEqualTo("0.2.260917008");
        await Assert.That(dto.PoolMemberCpuRequest).IsEqualTo("200m");
        await Assert.That(dto.PoolMemberMemoryLimit).IsEqualTo("1Gi");
        await Assert.That(dto.MaxConcurrentLeasesPerTenant).IsEqualTo(2);
        await Assert.That(dto.SharingMode).IsEqualTo(LendingScope.Descendants);
    }

    [Test]
    public async Task GetLentPoolDetails_Members_AreCountedAndNeverNamed()
    {
        var sut = CreateSut();
        _repo.TryGetAdapterPoolDetailsAsync(LenderTenantId, PoolRtId.ToString()).Returns(SampleDetails());
        _lendingScopeResolver
            .MayLendAsync(LenderTenantId, BorrowerTenantId, Arg.Any<LendingScope>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _connectionManager.GetMembers(LenderTenantId, PoolRtId.ToString()).Returns([
            Member("m-1", new LeaseDto { LeaseId = "l-1", TenantId = "someone-else" }),
            Member("m-2"),
            Member("m-3", draining: true)
        ]);
        _leaseScheduler.GetQueueAsync(LenderTenantId, Arg.Any<OctoObjectId>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var dto = ((await sut.GetLentPoolDetailsAsync(LenderTenantId, PoolRtId.ToString())) as OkObjectResult)!
            .Value as LentAdapterPoolDetailsDto;

        using var _ = Assert.Multiple();
        await Assert.That(dto!.Members.Connected).IsEqualTo(3);
        await Assert.That(dto.Members.Busy).IsEqualTo(1);
        await Assert.That(dto.Members.Draining).IsEqualTo(1);
        // Per-instance, and the wire says so rather than a surface having to remember it.
        await Assert.That(dto.Members.IsPartialView).IsTrue();
    }

    // ------------------------------------------------------------------ the queue split

    [Test]
    public async Task BuildBorrowerQueueView_OtherTenantsEntries_SurviveOnlyAsCounts()
    {
        var entries = new[]
        {
            Waiting("mine-1", BorrowerTenantId, position: 1, queuedAt: new DateTime(2026, 9, 19, 10, 0, 0, DateTimeKind.Utc)),
            Waiting("mine-2", BorrowerTenantId, position: 2, queuedAt: new DateTime(2026, 9, 19, 10, 5, 0, DateTimeKind.Utc)),
            Waiting("theirs-1", "salzburgdev", position: 1, queuedAt: new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc)),
            Waiting("theirs-2", "meshmakers", position: 1, queuedAt: new DateTime(2026, 9, 19, 9, 30, 0, DateTimeKind.Utc)),
            Leased("theirs-3", "salzburgdev", "member-7")
        };

        var view = AdapterPoolController.BuildBorrowerQueueView(entries, BorrowerTenantId);

        using var _ = Assert.Multiple();
        await Assert.That(view.MyEntries.Select(e => e.ExecutionId)).IsEquivalentTo(new[] { "mine-1", "mine-2" });
        await Assert.That(view.MyWaitingCount).IsEqualTo(2);
        await Assert.That(view.MyLeasedCount).IsEqualTo(0);
        await Assert.That(view.OtherTenantsWaitingCount).IsEqualTo(2);
        await Assert.That(view.OtherTenantsLeasedCount).IsEqualTo(1);
        await Assert.That(view.OtherTenantsInRotation).IsEqualTo(2);
    }

    [Test]
    public async Task BuildBorrowerQueueView_OwnLeasedEntry_CountsAsLeasedAndNotAsWaiting()
    {
        var entries = new[]
        {
            Leased("mine-running", BorrowerTenantId, "member-1"),
            Waiting("mine-waiting", BorrowerTenantId, position: 1,
                queuedAt: new DateTime(2026, 9, 19, 11, 0, 0, DateTimeKind.Utc))
        };

        var view = AdapterPoolController.BuildBorrowerQueueView(entries, BorrowerTenantId);

        using var _ = Assert.Multiple();
        await Assert.That(view.MyLeasedCount).IsEqualTo(1);
        await Assert.That(view.MyWaitingCount).IsEqualTo(1);
        // The oldest WAITING item, not the oldest item — a running execution is not waiting for
        // anything, and reporting it as the oldest wait would misprice the queue.
        await Assert.That(view.MyOldestQueuedAtUtc)
            .IsEqualTo(new DateTime(2026, 9, 19, 11, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public async Task BuildBorrowerQueueView_TenantIdCasing_DoesNotSplitOneTenantInTwo()
    {
        var entries = new[]
        {
            Waiting("mine", "GastroAcker", position: 1,
                queuedAt: new DateTime(2026, 9, 19, 10, 0, 0, DateTimeKind.Utc)),
            Waiting("theirs", "SALZBURGDEV", position: 1,
                queuedAt: new DateTime(2026, 9, 19, 10, 0, 0, DateTimeKind.Utc)),
            Waiting("theirs-again", "salzburgdev", position: 2,
                queuedAt: new DateTime(2026, 9, 19, 10, 1, 0, DateTimeKind.Utc))
        };

        var view = AdapterPoolController.BuildBorrowerQueueView(entries, BorrowerTenantId);

        using var _ = Assert.Multiple();
        await Assert.That(view.MyEntries.Count).IsEqualTo(1);
        await Assert.That(view.OtherTenantsInRotation).IsEqualTo(1);
    }

    [Test]
    public async Task BuildBorrowerQueueView_EmptyQueue_IsAnIdlePoolAndNotAnError()
    {
        var view = AdapterPoolController.BuildBorrowerQueueView([], BorrowerTenantId);

        using var _ = Assert.Multiple();
        await Assert.That(view.MyEntries).IsEmpty();
        await Assert.That(view.OtherTenantsInRotation).IsEqualTo(0);
        await Assert.That(view.MyOldestQueuedAtUtc).IsNull();
    }

    private static AdapterPoolQueueEntry Waiting(string executionId, string tenantId, int position,
        DateTime queuedAt)
    {
        return new AdapterPoolQueueEntry(executionId, tenantId, null, $"pipeline of {tenantId}", 1, queuedAt,
            position, 0, null, null);
    }

    private static AdapterPoolQueueEntry Leased(string executionId, string tenantId, string memberId)
    {
        return new AdapterPoolQueueEntry(executionId, tenantId, null, $"pipeline of {tenantId}", 1,
            new DateTime(2026, 9, 19, 8, 0, 0, DateTimeKind.Utc), 0, 0, memberId,
            new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc));
    }
}
