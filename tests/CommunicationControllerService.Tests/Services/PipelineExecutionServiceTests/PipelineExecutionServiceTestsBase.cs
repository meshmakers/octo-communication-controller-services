using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.PipelineExecutionServiceTests;

internal abstract class PipelineExecutionServiceTestsBase
{
    protected const string TenantId = "tenantId";
    protected readonly PipelineExecutionService PipelineExecutionService;
    protected readonly IAdapterCache AdapterCache;
    protected readonly ICommunicationRepository CommunicationRepository;
    protected readonly IAdapterCachePublish AdapterCachePublish;
    protected readonly ICommunicationEventService CommunicationEventService;
    protected readonly IWorkloadLifecycleService WorkloadLifecycleService;
    protected readonly AdapterTenant AdapterTenant;

    [SuppressMessage("Substitute creation", "NS2002:Constructor parameters count mismatch.")]
    protected PipelineExecutionServiceTestsBase()
    {
        AdapterCache = Substitute.For<IAdapterCache>();
        CommunicationRepository = Substitute.For<ICommunicationRepository>();
        AdapterCachePublish = Substitute.For<IAdapterCachePublish>();
        CommunicationEventService = Substitute.For<ICommunicationEventService>();
        WorkloadLifecycleService = Substitute.For<IWorkloadLifecycleService>();
        PipelineExecutionService = new PipelineExecutionService(CommunicationRepository, AdapterCache,
            CommunicationEventService, WorkloadLifecycleService);
        AdapterTenant = new AdapterTenant(AdapterCachePublish, TenantId);

        InitAdapterCache();
    }

    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    private void InitAdapterCache()
    {
        AdapterCache.TryGetTenant(TenantId, out Arg.Any<AdapterTenant?>())
            .Returns(x =>
            {
                x[1] = AdapterTenant;
                return true;
            });
    }
}
