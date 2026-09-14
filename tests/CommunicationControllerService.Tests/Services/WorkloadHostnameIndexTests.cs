using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

/// <summary>
///     AB#4923: the activator can only attribute an inbound request through this index, so a miss
///     is a 404 for a request the client expected to reach an adapter.
/// </summary>
internal class WorkloadHostnameIndexTests
{
    private const string TenantId = "acme";

    private readonly IAdapterCache _adapterCache = Substitute.For<IAdapterCache>();
    private readonly ICommunicationRepository _repository = Substitute.For<ICommunicationRepository>();
    private readonly WorkloadHostnameIndex _index;

    public WorkloadHostnameIndexTests()
    {
        _adapterCache.GetEnabledTenantIds().Returns([TenantId]);

        var options = Microsoft.Extensions.Options.Options.Create(new CommunicationControllerOptions());
        // Real resolver: hostname templates are part of what the index has to get right, and the
        // resolver is pure.
        var optionsMonitor = Substitute.For<IOptionsMonitor<CommunicationControllerOptions>>();
        optionsMonitor.CurrentValue.Returns(options.Value);
        var resolver = new WorkloadTemplateResolver(optionsMonitor);
        _index = new WorkloadHostnameIndex(NullLogger<WorkloadHostnameIndex>.Instance, _adapterCache,
            _repository, resolver, options);
    }

    private RtAdapter IngressWorkload(string hostname)
    {
        var adapter = RtEntityCreator.CreateAdapter();
        adapter.IngressEnabled = true;
        adapter.Hostname = hostname;
        return adapter;
    }

    private void ReturnWorkloads(params RtDeployableWorkload[] workloads)
    {
        _repository.GetWorkloadsAsync(TenantId).Returns(workloads);
    }

    [Test]
    public async Task IngressEnabledWorkload_IsResolvableByItsHostname()
    {
        // Arrange
        var adapter = IngressWorkload("adapter-acme.test-2.mm.cloud");
        ReturnWorkloads(adapter);

        // Act
        await _index.RefreshAsync();

        // Assert
        await Assert.That(_index.TryResolve("adapter-acme.test-2.mm.cloud", out var target)).IsTrue();
        await Assert.That(target!.TenantId).IsEqualTo(TenantId);
        await Assert.That(target.WorkloadRtId).IsEqualTo(adapter.RtId);
    }

    [Test]
    public async Task HostnameMatch_IsCaseInsensitive()
    {
        // Arrange — DNS is case-insensitive, and a client is free to send any casing.
        ReturnWorkloads(IngressWorkload("Adapter-Acme.test-2.mm.cloud"));

        // Act
        await _index.RefreshAsync();

        // Assert
        await Assert.That(_index.TryResolve("adapter-acme.TEST-2.mm.cloud", out _)).IsTrue();
    }

    [Test]
    public async Task WorkloadWithoutIngress_IsNotIndexed()
    {
        // Arrange — a cluster-internal workload has no public hostname to be reached under.
        var adapter = IngressWorkload("adapter-acme.test-2.mm.cloud");
        adapter.IngressEnabled = false;
        ReturnWorkloads(adapter);

        // Act
        await _index.RefreshAsync();

        // Assert
        await Assert.That(_index.TryResolve("adapter-acme.test-2.mm.cloud", out _)).IsFalse();
    }

    [Test]
    public async Task UnknownHost_IsAMiss()
    {
        // Arrange — the controller's own API hostname is the common case and must fall through.
        ReturnWorkloads(IngressWorkload("adapter-acme.test-2.mm.cloud"));

        // Act
        await _index.RefreshAsync();

        // Assert
        await Assert.That(_index.TryResolve("communication.test-2.mm.cloud", out _)).IsFalse();
        await Assert.That(_index.TryResolve(null, out _)).IsFalse();
    }

    [Test]
    public async Task UnresolvableHostnamePlaceholder_IsSkippedWithoutFailingTheRefresh()
    {
        // Arrange — the entity stores the template; an unconfigured domain cannot be matched
        // against a real Host header, so the entry is dropped rather than indexed verbatim.
        ReturnWorkloads(IngressWorkload("adapter.{{domain.nope}}"), IngressWorkload("good.test-2.mm.cloud"));

        // Act
        await _index.RefreshAsync();

        // Assert
        await Assert.That(_index.TryResolve("adapter.{{domain.nope}}", out _)).IsFalse();
        await Assert.That(_index.TryResolve("good.test-2.mm.cloud", out _)).IsTrue();
    }

    [Test]
    public async Task UnreadableTenant_DoesNotEmptyTheIndex()
    {
        // Arrange — an index that collapses on one bad tenant would 404 every other tenant's
        // hibernated workloads.
        _adapterCache.GetEnabledTenantIds().Returns([TenantId, "broken"]);
        ReturnWorkloads(IngressWorkload("adapter-acme.test-2.mm.cloud"));
        _repository.GetWorkloadsAsync("broken")
            .Returns<IReadOnlyCollection<RtDeployableWorkload>>(_ => throw new InvalidOperationException("boom"));

        // Act
        await _index.RefreshAsync();

        // Assert
        await Assert.That(_index.TryResolve("adapter-acme.test-2.mm.cloud", out _)).IsTrue();
    }

    /// <summary>
    ///     The forwarding address is the workload's Service, which the operator names after the helm
    ///     release. These cases pin the rule against the operator's <c>K8sNaming.DnsName</c> — the
    ///     two implementations are separate and only a test keeps them honest.
    /// </summary>
    [Test]
    [Arguments("meshdev", "670000000000000000000002", "meshdev-670000000000000000000002")]
    [Arguments("ACME", "670000000000000000000002", "acme-670000000000000000000002")]
    [Arguments("a b", "670000000000000000000002", "a-b-670000000000000000000002")]
    [Arguments("-lead-", "670000000000000000000002", "lead-670000000000000000000002")]
    public async Task ReleaseName_MatchesTheOperatorsRule(string tenantId, string workloadRtId, string expected)
    {
        await Assert.That(WorkloadHostnameIndex.ReleaseName(tenantId, workloadRtId)).IsEqualTo(expected);
    }

    [Test]
    public async Task ReleaseName_IsCappedAtHelmsReleaseNameLimit()
    {
        var name = WorkloadHostnameIndex.ReleaseName(new string('t', 60), "670000000000000000000002");

        await Assert.That(name.Length).IsLessThanOrEqualTo(53);
        await Assert.That(name.EndsWith('-')).IsFalse();
    }

    [Test]
    public async Task AdapterPool_IsNeverIndexed_EvenWithIngressEnabled()
    {
        // AB#4924. This index publishes an address built from the release name alone, resolved in
        // the CONTROLLER's namespace — and a pool runs in the platform namespace. An entry for one
        // would point the activator at a Service that is not there, or at a same-named one that is.
        // A pool is reached by being leased, never by an inbound request, so it has no hostname to
        // claim in the first place.
        var pool = new RtAdapterPool
        {
            RtId = Meshmakers.Octo.ConstructionKit.Contracts.OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkAdapterPoolTypeId,
            Name = "acme-pool",
            IngressEnabled = true,
            Hostname = "pool-acme.test-2.mm.cloud",
        };
        ReturnWorkloads(pool);

        await _index.RefreshAsync();

        await Assert.That(_index.TryResolve("pool-acme.test-2.mm.cloud", out _)).IsFalse();
    }

    [Test]
    public async Task AdapterPool_DoesNotDisplaceAnAdapterClaimingTheSameHostname()
    {
        // The pool is skipped before the first-one-wins map insert, so an adapter that legitimately
        // owns the hostname keeps it regardless of enumeration order.
        var pool = new RtAdapterPool
        {
            RtId = Meshmakers.Octo.ConstructionKit.Contracts.OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkAdapterPoolTypeId,
            Name = "acme-pool",
            IngressEnabled = true,
            Hostname = "shared.test-2.mm.cloud",
        };
        var adapter = IngressWorkload("shared.test-2.mm.cloud");
        ReturnWorkloads(pool, adapter);

        await _index.RefreshAsync();

        await Assert.That(_index.TryResolve("shared.test-2.mm.cloud", out var target)).IsTrue();
        await Assert.That(target!.WorkloadRtId).IsEqualTo(adapter.RtId);
    }
}
