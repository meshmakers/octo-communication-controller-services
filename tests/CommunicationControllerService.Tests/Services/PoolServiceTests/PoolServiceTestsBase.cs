using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.ConstructionKit.Contracts;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.PoolServiceTests;

internal abstract class PoolServiceTestsBase
{
    protected const string TenantId = "tenantId";
    protected const string PoolName = "default";
    protected const string ConnectionId = "connectionId";
    protected static readonly OctoObjectId PoolRtId = OctoObjectId.GenerateNewId();

    protected readonly ICommunicationRepository CommunicationRepository;
    protected readonly IPoolCache PoolCache;
    protected readonly ICommunicationEventService CommunicationEventService;
    protected readonly IOperatorConnectionManager OperatorConnectionManager;
    protected readonly IWorkloadEncryptionService EncryptionService;
    protected readonly IWorkloadTemplateResolver TemplateResolver;
    protected readonly IWorkloadOnDemandCapabilityService OnDemandCapabilityService;
    protected readonly IPipelineServiceAccountProvisioningService ServiceAccountProvisioningService;
    protected readonly IPoolCachePublish PoolCachePublish;
    protected readonly PoolTenant PoolTenant;
    protected readonly PoolService PoolService;

    [SuppressMessage("Substitute creation", "NS2002:Constructor parameters count mismatch.")]
    [SuppressMessage("Argument matchers", "NS3003:Multiple matchers of same type",
        Justification = "TryResolve has three string? out params (resolved + unknownPlaceholder); the matchers are unambiguous by position.")]
    protected PoolServiceTestsBase()
    {
        CommunicationRepository = Substitute.For<ICommunicationRepository>();
        PoolCache = Substitute.For<IPoolCache>();
        CommunicationEventService = Substitute.For<ICommunicationEventService>();
        OperatorConnectionManager = Substitute.For<IOperatorConnectionManager>();
        // Default: no other operator connection is still claiming any pool.
        // The multi-claim guard in SetCommunicationStateOfflineAsync needs a
        // non-null IReadOnlyList<string> back from this call; tests that
        // exercise the multi-claim path override the return value.
        OperatorConnectionManager
            .GetConnectionsForPool(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Array.Empty<string>());
        EncryptionService = Substitute.For<IWorkloadEncryptionService>();
        // Default: Decrypt passes the value through unchanged (so non-secret
        // tests don't have to set up Decrypt). Specific tests override this.
        EncryptionService.Decrypt(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        TemplateResolver = Substitute.For<IWorkloadTemplateResolver>();
        // Default: literal pass-through (no placeholders configured). Tests
        // that exercise template behaviour override TryResolve explicitly.
        TemplateResolver.AvailableDomains
            .Returns(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        TemplateResolver.AvailableServiceUrls
            .Returns(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        TemplateResolver
            .TryResolve(Arg.Any<string?>(), Arg.Any<WorkloadTemplateContext>(),
                out Arg.Any<string?>(), out Arg.Any<string?>())
            .Returns(ci =>
            {
                // Default behaviour: pass the input through unchanged so tests
                // that don't touch the resolver see literal values. Use
                // positional ci.ArgAt<T> for the two out params (positions 2
                // and 3 after template + context).
                ci[2] = ci.ArgAt<string?>(0);
                ci[3] = (string?)null;
                return true;
            });
        OnDemandCapabilityService = Substitute.For<IWorkloadOnDemandCapabilityService>();
        // Default: every workload is on-demand capable (AB#4984). Tests that exercise
        // the capability rejection override EvaluateAsync explicitly.
        OnDemandCapabilityService
            .EvaluateAsync(Arg.Any<string>(), Arg.Any<RtEntityId>())
            .Returns(new OnDemandCapabilityResult(true, []));
        PoolCachePublish = Substitute.For<IPoolCachePublish>();
        PoolTenant = new PoolTenant(PoolCachePublish, TenantId);

        // AB#5027: the deploy path provisions the adapter's pipeline service account. Substituted
        // here — the real behaviour is covered by PipelineServiceAccountProvisioningServiceTests;
        // what the pool suite asserts is that the call is made for Adapters and only for Adapters.
        ServiceAccountProvisioningService = Substitute.For<IPipelineServiceAccountProvisioningService>();

        PoolService = new PoolService(
            CommunicationRepository,
            PoolCache,
            CommunicationEventService,
            OperatorConnectionManager,
            EncryptionService,
            TemplateResolver,
            OnDemandCapabilityService,
            ServiceAccountProvisioningService);
    }

    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    protected void GivenTenantInCache()
    {
        PoolCache.TryGetTenant(TenantId, out Arg.Any<PoolTenant?>())
            .Returns(x =>
            {
                x[1] = PoolTenant;
                return true;
            });
    }

    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    protected void GivenTenantNotInCache()
    {
        PoolCache.TryGetTenant(TenantId, out Arg.Any<PoolTenant?>())
            .Returns(false);
    }

    protected Pool AddPoolToTenant(string poolName = PoolName, string connectionId = ConnectionId)
    {
        return PoolTenant.AddPool(poolName, PoolRtId, connectionId);
    }
}
