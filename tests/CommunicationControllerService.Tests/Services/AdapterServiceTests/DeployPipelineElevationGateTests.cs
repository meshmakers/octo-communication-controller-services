using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

/// <summary>
/// AB#5128 (Epic AB#4979): the deploy guard authorizes privilege elevation. A pipeline that runs a
/// data node under Identity=ServiceAccount or System (AB#5127) escalates beyond the caller's own
/// rights, so an unauthorized caller is refused (naming the node) on both deploy paths; and the
/// non-blocking confused-deputy lint warns when an elevated node targets a raw caller-controlled
/// path. The base arranges a resolvable service account + verifiable identity, so those sibling
/// guards never interfere here.
/// </summary>
internal class DeployPipelineElevationGateTests : AdapterServiceTestsBase
{
    // An elevated node (Identity=System) whose target is a raw caller-controlled path — both the
    // elevation itself and a confused-deputy hazard.
    private const string ElevatedConfusedDeputyDefinition =
        """
        triggers:
          - type: FromHttpRequest@2
            method: Post
            path: /elevated
        transformations:
          - type: GetRtEntitiesById@1
            identity: System
            rtIdsPath: $.body.rtId
        """;

    // An elevated node (Identity=ServiceAccount) whose target is a constant — elevated, but no
    // confused-deputy hazard, so the lint must stay silent.
    private const string ElevatedConstantTargetDefinition =
        """
        triggers:
          - type: FromHttpRequest@2
            method: Post
            path: /elevated
        transformations:
          - type: GetRtEntitiesByType@1
            identity: ServiceAccount
            ckTypeId: SomeCkType
        """;

    // No elevated node: every node runs as the default Caller identity.
    private const string NonElevatedDefinition =
        """
        triggers:
          - type: FromHttpRequest@2
            method: Post
            path: /plain
        transformations:
          - type: GetRtEntitiesById@1
            rtIdsPath: $.body.rtId
        """;

    private (RtAdapter Adapter, RtDataFlow DataFlow, RtPipeline Pipeline) ArrangeDeployablePipeline(
        string pipelineDefinition)
    {
        var rtAdapter = RtEntityCreator.CreateAdapter();
        rtAdapter.Name = "mesh-adapter";
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline = RtEntityCreator.CreatePipeline(pipelineDefinition);

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            null,
            []
        ));

        CommunicationRepository.GetPipelineAsync(TenantId, rtPipeline.ToRtEntityId()).Returns(rtPipeline);
        CommunicationRepository.GetDataFlowByPipelineAsync(TenantId, rtPipeline.RtId).Returns(rtDataFlow);
        CommunicationRepository.GetAdapterAsync(TenantId, rtAdapter.ToRtEntityId()).Returns(rtAdapter);
        CommunicationRepository.GetWorkloadByRtIdAsync(TenantId, rtAdapter.RtId).Returns(rtAdapter);
        CommunicationRepository.GetAdapterByPipelineAsync(TenantId, rtPipeline.ToRtEntityId()).Returns(rtAdapter);
        CommunicationRepository.GetPipelinesAsync(TenantId, rtAdapter.ToRtEntityId()).Returns([rtPipeline]);
        CommunicationRepository.GetPipelinesAsync(TenantId, rtDataFlow.RtId).Returns([rtPipeline]);
        CommunicationRepository.GetConfigurationsByPipelineAsync(TenantId, rtPipeline.RtId)
            .Returns(Task.FromResult<IEnumerable<RtConfiguration>>([]));

        return (rtAdapter, rtDataFlow, rtPipeline);
    }

    [Test]
    public async Task DeployPipelineAsync_ElevatedNode_UnauthorizedCaller_IsRejectedNamingTheNode()
    {
        var (adapter, _, pipeline) = ArrangeDeployablePipeline(ElevatedConfusedDeputyDefinition);
        SetCaller(withUserManagementRole: false);

        var ex = await Assert.ThrowsAsync<Exception>(async () =>
            await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId()));

        using var _ = Assert.Multiple();
        await Assert.That(ex!).IsTypeOf<AdapterServiceException>();
        await Assert.That(ex!.Message).Contains("AB#5128");
        await Assert.That(ex!.Message).Contains("GetRtEntitiesById@1");
        await Assert.That(ex!.Message).Contains("System");
        await Assert.That(ex!.Message).Contains("UserManagement");
        // Refused before any state write reaches the adapter.
        await AdapterHubCallbacks.DidNotReceiveWithAnyArgs()
            .AdapterConfigurationUpdatedAsync(Arg.Any<string>(), Arg.Any<AdapterConfigurationDto>());
    }

    [Test]
    public async Task DeployPipelineAsync_ElevatedNode_AuthorizedCaller_Deploys()
    {
        var (adapter, _, pipeline) = ArrangeDeployablePipeline(ElevatedConstantTargetDefinition);
        SetCaller(withUserManagementRole: true);

        await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId());

        await AdapterHubCallbacks.Received(1)
            .AdapterConfigurationUpdatedAsync(TenantId, Arg.Any<AdapterConfigurationDto>());
    }

    [Test]
    public async Task DeployPipelineAsync_NoElevatedNode_UnauthorizedCaller_Deploys()
    {
        var (adapter, _, pipeline) = ArrangeDeployablePipeline(NonElevatedDefinition);
        SetCaller(withUserManagementRole: false);

        await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId());

        await AdapterHubCallbacks.Received(1)
            .AdapterConfigurationUpdatedAsync(TenantId, Arg.Any<AdapterConfigurationDto>());
    }

    [Test]
    public async Task DeployPipelineAsync_ElevatedNode_OptionDisabled_SkipsAuthorizationAndDeploys()
    {
        var (adapter, _, pipeline) = ArrangeDeployablePipeline(ElevatedConstantTargetDefinition);
        GuardOptions.CheckElevation = false;
        SetCaller(withUserManagementRole: false);

        await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId());

        await AdapterHubCallbacks.Received(1)
            .AdapterConfigurationUpdatedAsync(TenantId, Arg.Any<AdapterConfigurationDto>());
    }

    [Test]
    public async Task DeployPipelineAsync_ElevatedNode_SystemInitiated_NoPrincipal_Deploys()
    {
        var (adapter, _, pipeline) = ArrangeDeployablePipeline(ElevatedConstantTargetDefinition);
        // No SetCaller: HttpContext is null — a system-initiated deploy (e.g. post-provisioning).

        await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId());

        await AdapterHubCallbacks.Received(1)
            .AdapterConfigurationUpdatedAsync(TenantId, Arg.Any<AdapterConfigurationDto>());
    }

    [Test]
    public async Task DeployPipelineAsync_ElevatedNode_RawBodyTarget_EmitsConfusedDeputyWarning()
    {
        var (adapter, _, pipeline) = ArrangeDeployablePipeline(ElevatedConfusedDeputyDefinition);
        SetCaller(withUserManagementRole: true);

        await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId());

        await CommunicationEventService.Received(1).StoreWarningEventAsync(TenantId,
            Arg.Is<string>(m => m.Contains("Confused-deputy") && m.Contains("$.body.rtId")
                                && m.Contains("rtIdsPath")),
            pipeline.ToRtEntityId());
    }

    [Test]
    public async Task DeployPipelineAsync_ElevatedNode_ConstantTarget_EmitsNoWarning()
    {
        var (adapter, _, pipeline) = ArrangeDeployablePipeline(ElevatedConstantTargetDefinition);
        SetCaller(withUserManagementRole: true);

        await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId());

        await CommunicationEventService.DidNotReceive().StoreWarningEventAsync(TenantId,
            Arg.Is<string>(m => m.Contains("Confused-deputy")),
            Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task DeployDataFlowAsync_ElevatedPipeline_UnauthorizedCaller_IsRejectedAndReachesNoAdapter()
    {
        var (_, dataFlow, _) = ArrangeDeployablePipeline(ElevatedConfusedDeputyDefinition);
        SetCaller(withUserManagementRole: false);

        var ex = await Assert.ThrowsAsync<Exception>(async () =>
            await AdapterService.DeployDataFlowAsync(TenantId, dataFlow.RtId));

        using var _ = Assert.Multiple();
        await Assert.That(ex!).IsTypeOf<AdapterServiceException>();
        await Assert.That(ex!.Message).Contains("AB#5128");
        await Assert.That(ex!.Message).Contains("GetRtEntitiesById@1");
        await AdapterHubCallbacks.DidNotReceiveWithAnyArgs()
            .AdapterConfigurationUpdatedAsync(Arg.Any<string>(), Arg.Any<AdapterConfigurationDto>());
    }

    [Test]
    public async Task DeployDataFlowAsync_ElevatedPipeline_AuthorizedCaller_Deploys()
    {
        var (_, dataFlow, _) = ArrangeDeployablePipeline(ElevatedConstantTargetDefinition);
        SetCaller(withUserManagementRole: true);

        await AdapterService.DeployDataFlowAsync(TenantId, dataFlow.RtId);

        await AdapterHubCallbacks.Received(1)
            .AdapterConfigurationUpdatedAsync(TenantId, Arg.Any<AdapterConfigurationDto>());
    }
}
