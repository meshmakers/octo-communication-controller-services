using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.Runtime.Contracts;
using Microsoft.Extensions.Options;
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
    protected readonly ICommunicationEventService CommunicationEventService;
    protected readonly IPipelineSchemaValidator PipelineSchemaValidator;
    protected readonly AdapterTenant AdapterTenant;

    [SuppressMessage("Substitute creation", "NS2002:Constructor parameters count mismatch.")]
    protected AdapterServiceTestsBase()
    {
        AdapterHubCallbacks = Substitute.For<IAdapterHubCallbacks>();
        AdapterCache = Substitute.For<IAdapterCache>();
        CommunicationRepository = Substitute.For<ICommunicationRepository>();
        AdapterCachePublish = Substitute.For<IAdapterCachePublish>();
        CommunicationEventService = Substitute.For<ICommunicationEventService>();
        PipelineSchemaValidator = Substitute.For<IPipelineSchemaValidator>();
        var options = Substitute.For<IOptions<CommunicationControllerOptions>>();
        options.Value.Returns(new CommunicationControllerOptions());
        AdapterService = new AdapterService(CommunicationRepository, AdapterCache, AdapterHubCallbacks,
            CommunicationEventService, PipelineSchemaValidator, options);
        AdapterTenant = new AdapterTenant(AdapterCachePublish, TenantId);

        InitAdapterCache();
        SimulateAdapterDeploymentCallback();
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

    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    private void SimulateAdapterDeploymentCallback()
    {
        AdapterHubCallbacks.AdapterConfigurationUpdatedAsync(Arg.Any<string>(), Arg.Any<AdapterConfigurationDto>())
            .Returns(callInfo =>
            {
                var adapterConfiguration = callInfo.Arg<AdapterConfigurationDto>();
                _ = Task.Run(async () =>
                {
                    await AdapterService.UpdateConfigurationStateAsync(TenantId, adapterConfiguration.AdapterRtEntityId,
                        new DeploymentResult { IsSuccess = true });
                });
                return Task.CompletedTask;
            });
    }
    
    protected void InitAdapterConfiguration(RtAdapter rtAdapter, RtDataFlow rtDataFlow,
        List<RtPipeline> rtPipelines)
    {
        CommunicationRepository.GetAdapterAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns(rtAdapter);
        CommunicationRepository.GetPipelinesAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns(rtPipelines);
        foreach (var rtPipeline in rtPipelines)
        {
            CommunicationRepository.GetDataFlowByPipelineAsync(TenantId, rtPipeline.RtId)
                .Returns(rtDataFlow);
        }
    }
}
