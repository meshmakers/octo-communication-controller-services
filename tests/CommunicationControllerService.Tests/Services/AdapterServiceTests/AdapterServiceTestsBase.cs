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

    /// <summary>
    /// AB#5111: the options instance behind both the service's IOptions and the real
    /// <see cref="WorkloadTemplateResolver" /> the service uses to resolve the IssuerUri token —
    /// tests mutate <c>ServiceUrls</c> / <c>AuthorityUrl</c> on it (the resolver re-snapshots per
    /// call, so no rebuild is needed).
    /// </summary>
    protected readonly CommunicationControllerOptions ControllerOptions = new();

    /// <summary>
    /// AB#5112: the base arranges a verifiable identity — the reader answers "Found" for any
    /// client, so the hardened deploy guard passes everywhere it is not the subject under test.
    /// Guard tests re-stub this to NotFound / Unavailable.
    /// </summary>
    protected readonly IIdentityClientReader IdentityClientReader = Substitute.For<IIdentityClientReader>();

    /// <summary>
    /// AB#5112: mutable guard options (default: identity-client check ON, matching production);
    /// the option-disabled pass-through test flips <see cref="ServiceAccountGuardOptions.CheckIdentityClient" />.
    /// </summary>
    protected readonly ServiceAccountGuardOptions GuardOptions = new();

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
        options.Value.Returns(ControllerOptions);
        var optionsMonitor = Substitute.For<IOptionsMonitor<CommunicationControllerOptions>>();
        optionsMonitor.CurrentValue.Returns(ControllerOptions);
        AdapterConnectionTracker = new AdapterConnectionTracker();
        OnDemandCapabilityService = new WorkloadOnDemandCapabilityService(CommunicationRepository,
            AdapterCache, PipelineDefinitionService);
        ServiceAccountResolver = new PipelineServiceAccountResolver(CommunicationRepository);
        DefaultAdapterServiceAccount = RtEntityCreator.CreateServiceAccountConfiguration();
        var guardOptions = Substitute.For<IOptions<ServiceAccountGuardOptions>>();
        guardOptions.Value.Returns(GuardOptions);
        AdapterService = new AdapterService(CommunicationRepository, AdapterCache, AdapterHubCallbacks,
            CommunicationEventService, PipelineSchemaValidator, PipelineDefinitionService,
            AdapterConnectionTracker, options, WorkloadLifecycleService, OnDemandCapabilityService,
            ServiceAccountResolver,
            // Real resolver (AB#5111), same reasoning as the service-account resolver above: the
            // IssuerUri token resolution in the configuration projection runs the real machinery.
            new WorkloadTemplateResolver(optionsMonitor),
            IdentityClientReader, guardOptions);
        AdapterTenant = new AdapterTenant(AdapterCachePublish, TenantId);

        InitAdapterCache();
        InitAdapterServiceAccount();
        InitIdentityClientReader();
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

    /// <summary>
    /// AB#5112: default the identity to "client exists", so the hardened guard passes everywhere
    /// it is not the subject under test (mirrors <see cref="InitAdapterServiceAccount" />).
    /// </summary>
    private void InitIdentityClientReader()
    {
        IdentityClientReader
            .GetClientAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(callInfo => new IdentityClientLookup(IdentityClientLookupStatus.Found,
                new ClientDto { ClientId = callInfo.ArgAt<string>(1) }, null, null));
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
