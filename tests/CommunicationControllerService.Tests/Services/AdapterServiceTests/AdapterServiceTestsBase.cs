using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v2;
using Meshmakers.Octo.Runtime.Contracts;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

internal abstract class AdapterServiceTestsBase
{
    protected readonly string TenantId = "tenantId";
    protected readonly string ConnectionId = "connectionId";
    protected readonly AdapterService AdapterService;
    protected readonly IAdapterHubCallbacks AdapterHubCallbacks;
    protected readonly IAdapterCache AdapterCache;
    protected readonly ICommunicationRepository CommunicationRepository;
    protected readonly IAdapterCachePublish AdapterCachePublish;
    protected readonly AdapterTenant AdapterTenant;

    [SuppressMessage("Substitute creation", "NS2002:Constructor parameters count mismatch.")]
    protected AdapterServiceTestsBase()
    {
        AdapterHubCallbacks = Substitute.For<IAdapterHubCallbacks>();
        AdapterCache = Substitute.For<IAdapterCache>();
        CommunicationRepository = Substitute.For<ICommunicationRepository>();
        AdapterCachePublish = Substitute.For<IAdapterCachePublish>();
        AdapterService = new AdapterService(CommunicationRepository, AdapterCache, AdapterHubCallbacks);
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
    
    protected void InitAdapterConfiguration(RtAdapter rtAdapter, RtDataPipeline rtDataPipeline,
        List<RtPipeline> rtPipelines)
    {
        CommunicationRepository.GetAdapterAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns(rtAdapter);
        CommunicationRepository.GetPipelinesAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns(rtPipelines);
        foreach (var rtPipeline in rtPipelines)
        {
            CommunicationRepository.GetDataPipelineByPipelineAsync(TenantId, rtPipeline.RtId)
                .Returns(rtDataPipeline);
        }
    }
}
