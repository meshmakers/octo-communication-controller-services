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
    protected readonly IPoolCachePublish PoolCachePublish;
    protected readonly PoolTenant PoolTenant;
    protected readonly PoolService PoolService;

    [SuppressMessage("Substitute creation", "NS2002:Constructor parameters count mismatch.")]
    protected PoolServiceTestsBase()
    {
        CommunicationRepository = Substitute.For<ICommunicationRepository>();
        PoolCache = Substitute.For<IPoolCache>();
        CommunicationEventService = Substitute.For<ICommunicationEventService>();
        OperatorConnectionManager = Substitute.For<IOperatorConnectionManager>();
        EncryptionService = Substitute.For<IWorkloadEncryptionService>();
        // Default: Decrypt passes the value through unchanged (so non-secret
        // tests don't have to set up Decrypt). Specific tests override this.
        EncryptionService.Decrypt(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        PoolCachePublish = Substitute.For<IPoolCachePublish>();
        PoolTenant = new PoolTenant(PoolCachePublish, TenantId);

        PoolService = new PoolService(
            CommunicationRepository,
            PoolCache,
            CommunicationEventService,
            OperatorConnectionManager,
            EncryptionService);
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
