using System.Diagnostics.CodeAnalysis;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.ConstructionKit.Contracts;
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
    protected readonly IPipelineDefinitionService PipelineDefinitionService;
    // Real tracker (simple, deterministic) so the Online/Offline write paths exercise real
    // liveness tracking and the reconciliation tests can observe HasLiveConnection end-to-end.
    protected readonly AdapterConnectionTracker AdapterConnectionTracker;
    protected readonly IWorkloadLifecycleService WorkloadLifecycleService = Substitute.For<IWorkloadLifecycleService>();
    // Real capability service (AB#4984) on top of the substituted repository/cache so the
    // OnDemand deploy gates run the real trigger classification against real YAML.
    protected readonly IWorkloadOnDemandCapabilityService OnDemandCapabilityService;
    // Real resolver (AB#5027) on top of the substituted repository so the deploy gate and the
    // configuration projection run the real override-beats-default resolution.
    protected readonly IPipelineServiceAccountResolver ServiceAccountResolver;
    /// <summary>
    /// AB#5027: the base arranges a properly provisioned tenant — every adapter resolves to this
    /// service account by default, so the mandatory-identity deploy gate is satisfied and the
    /// pre-existing suites keep exercising what they were written for. Tests for the gate itself
    /// re-stub <c>GetServiceAccountForAdapterAsync</c> to return null.
    /// </summary>
    protected readonly RtServiceAccountConfiguration DefaultAdapterServiceAccount;

    /// <summary>
    /// AB#5027: the shape the projected adapter default takes in a
    /// <see cref="PipelineConfigurationDto" />. Tests that pre-seed an "already deployed"
    /// configuration need it, otherwise the freshly built configuration differs from the cached
    /// one purely because of the projection.
    /// </summary>
    protected ConfigurationDto DefaultAdapterServiceAccountDto => new(
        DefaultAdapterServiceAccount.RtId,
        DefaultAdapterServiceAccount.CkTypeId!,
        DefaultAdapterServiceAccount.RtWellKnownName!,
        DefaultAdapterServiceAccount.Serialize());

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
        // Real parser (pure, dependency-free) so deprecated-node detection works against real YAML
        PipelineDefinitionService = new PipelineDefinitionService();
        var options = Substitute.For<IOptions<CommunicationControllerOptions>>();
        options.Value.Returns(new CommunicationControllerOptions());
        AdapterConnectionTracker = new AdapterConnectionTracker();
        OnDemandCapabilityService = new WorkloadOnDemandCapabilityService(CommunicationRepository,
            AdapterCache, PipelineDefinitionService);
        ServiceAccountResolver = new PipelineServiceAccountResolver(CommunicationRepository);
        DefaultAdapterServiceAccount = RtEntityCreator.CreateServiceAccountConfiguration();
        AdapterService = new AdapterService(CommunicationRepository, AdapterCache, AdapterHubCallbacks,
            CommunicationEventService, PipelineSchemaValidator, PipelineDefinitionService,
            AdapterConnectionTracker, options, WorkloadLifecycleService, OnDemandCapabilityService,
            ServiceAccountResolver);
        AdapterTenant = new AdapterTenant(AdapterCachePublish, TenantId);

        InitAdapterCache();
        InitAdapterServiceAccount();
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

    /// <summary>
    /// AB#5027: default the tenant to "adapter has a service account linked", so the mandatory
    /// deploy gate passes everywhere it is not the subject under test.
    /// </summary>
    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    private void InitAdapterServiceAccount()
    {
        CommunicationRepository
            .GetServiceAccountForAdapterAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>())
            .Returns(DefaultAdapterServiceAccount);
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
