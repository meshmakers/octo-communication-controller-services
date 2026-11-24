using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

/// <summary>
/// AB#9767 (Feature AB#8673): the deploy gate for the mesh adapter's cross-tenant nodes. Decision 3: a node with
/// Identity=System and a target tenant is refused. Decision 2: a target tenant taken from caller input is refused when
/// the node would act there with the pipeline's service account (Identity=ServiceAccount, or Caller in a pipeline that
/// can run without a verified caller), and only reported as a warning for the pure verified caller. Both rules hold
/// for an authorized deployer and with the elevation check switched off.
/// </summary>
internal class DeployPipelineCrossTenantGateTests : AdapterServiceTestsBase
{
    private const string VerifiedHttpTrigger =
        """
        triggers:
          - type: FromHttpRequest@2
            method: Post
            path: /cross
        """;

    private const string AnonymousHttpTrigger =
        """
        triggers:
          - type: FromHttpRequest@2
            method: Post
            path: /cross
            allowAnonymous: true
        """;

    private static string Definition(string triggers, string identity, string tenantIdPath) =>
        triggers + "\n" +
        $"""
         transformations:
           - type: ApplyChanges@2
             {identity}
             entityUpdatesPath: $.updates
             targetTenant:
               relation: Sibling
               tenantIdPath: {tenantIdPath}
         """;

    private (RtAdapter Adapter, RtPipeline Pipeline) ArrangeDeployablePipeline(string pipelineDefinition)
    {
        var rtAdapter = RtEntityCreator.CreateAdapter();
        rtAdapter.Name = "mesh-adapter";
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline = RtEntityCreator.CreatePipeline(pipelineDefinition);

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId,
            new AdapterConfigurationDto(rtAdapter.ToRtEntityId(), null, []));

        CommunicationRepository.GetPipelineAsync(TenantId, rtPipeline.ToRtEntityId()).Returns(rtPipeline);
        CommunicationRepository.GetDataFlowByPipelineAsync(TenantId, rtPipeline.RtId).Returns(rtDataFlow);
        CommunicationRepository.GetAdapterAsync(TenantId, rtAdapter.ToRtEntityId()).Returns(rtAdapter);
        CommunicationRepository.GetWorkloadByRtIdAsync(TenantId, rtAdapter.RtId).Returns(rtAdapter);
        CommunicationRepository.GetAdapterByPipelineAsync(TenantId, rtPipeline.ToRtEntityId()).Returns(rtAdapter);
        CommunicationRepository.GetPipelinesAsync(TenantId, rtAdapter.ToRtEntityId()).Returns([rtPipeline]);
        CommunicationRepository.GetPipelinesAsync(TenantId, rtDataFlow.RtId).Returns([rtPipeline]);
        CommunicationRepository.GetConfigurationsByPipelineAsync(TenantId, rtPipeline.RtId)
            .Returns(Task.FromResult<IEnumerable<RtConfiguration>>([]));

        return (rtAdapter, rtPipeline);
    }

    private async Task<Exception?> DeployAsync(string definition)
    {
        var (adapter, pipeline) = ArrangeDeployablePipeline(definition);
        // An authorized deployer: the refusals are about the pipeline, not about who deploys it.
        SetCaller(withUserManagementRole: true);
        try
        {
            await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId());
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    private async Task AssertNothingDeployed() =>
        await AdapterHubCallbacks.DidNotReceiveWithAnyArgs()
            .AdapterConfigurationUpdatedAsync(Arg.Any<string>(), Arg.Any<AdapterConfigurationDto>());

    // Decision 3 / abuse case 5: the system context never acts in another tenant, also with a constant target.
    [Test]
    public async Task SystemNodeWithTargetTenant_IsRefused()
    {
        var ex = await DeployAsync(
            """
            triggers:
              - type: FromPipelineTriggerEvent@1
            transformations:
              - type: GetRtEntitiesByType@1
                identity: System
                ckTypeId: Basic/Contact
                targetTenant:
                  relation: Parent
            """);

        using var _ = Assert.Multiple();
        await Assert.That(ex).IsTypeOf<AdapterServiceException>();
        await Assert.That(ex!.Message).Contains("AB#9767");
        await Assert.That(ex.Message).Contains("GetRtEntitiesByType@1[0] (System)");
        await AssertNothingDeployed();
    }

    [Test]
    public async Task SystemNodeWithTargetTenant_IsRefused_AlsoWithTheElevationCheckOff()
    {
        GuardOptions.CheckElevation = false;

        var ex = await DeployAsync(Definition(VerifiedHttpTrigger, "identity: System", "$.invoice.tenantId"));

        await Assert.That(ex).IsTypeOf<AdapterServiceException>();
        await AssertNothingDeployed();
    }

    // Decision 2, case ServiceAccount: the caller would choose the tenant the service account writes in.
    [Test]
    public async Task ServiceAccountWithCallerControlledTenantIdPath_IsRefused()
    {
        var ex = await DeployAsync(Definition(VerifiedHttpTrigger, "identity: ServiceAccount",
            "$.body.recipientTenantId"));

        using var _ = Assert.Multiple();
        await Assert.That(ex).IsTypeOf<AdapterServiceException>();
        await Assert.That(ex!.Message).Contains("AB#9767");
        await Assert.That(ex.Message).Contains("$.body.recipientTenantId");
        await Assert.That(ex.Message).Contains("ApplyChanges@2[0] (ServiceAccount)");
        await AssertNothingDeployed();
    }

    // Decision 2, case Caller with service-account fallback: an anonymous request has no verified caller, so the node
    // runs as the service account in a tenant the anonymous caller chose.
    [Test]
    public async Task CallerWithServiceAccountFallback_AndCallerControlledTenantIdPath_IsRefused()
    {
        var ex = await DeployAsync(Definition(AnonymousHttpTrigger, "identity: Caller", "$.query.tenant"));

        using var _ = Assert.Multiple();
        await Assert.That(ex).IsTypeOf<AdapterServiceException>();
        await Assert.That(ex!.Message).Contains("service-account fallback");
        await Assert.That(ex.Message).Contains("$.query.tenant");
        await AssertNothingDeployed();
    }

    // Decision 2, case pure Caller: allowed (the adapter proves the caller's own right in the target), but reported.
    [Test]
    public async Task PureCallerWithCallerControlledTenantIdPath_Deploys_WithAWarning()
    {
        var ex = await DeployAsync(Definition(VerifiedHttpTrigger, "identity: Caller", "$.body.recipientTenantId"));

        using var _ = Assert.Multiple();
        await Assert.That(ex).IsNull();
        await AdapterHubCallbacks.Received(1)
            .AdapterConfigurationUpdatedAsync(TenantId, Arg.Any<AdapterConfigurationDto>());
        await CommunicationEventService.Received(1).StoreWarningEventAsync(TenantId,
            Arg.Is<string>(m => m.Contains("$.body.recipientTenantId") && m.Contains("target tenant")),
            Arg.Any<RtEntityId>());
    }

    // Decision 2, data-driven target the pipeline controls: allowed without warning, the guard checks it at run time.
    [Test]
    public async Task ServiceAccountWithPipelineControlledTenantIdPath_Deploys_WithoutWarning()
    {
        var ex = await DeployAsync(Definition(AnonymousHttpTrigger, "identity: ServiceAccount",
            "$.invoice.recipientTenantId"));

        using var _ = Assert.Multiple();
        await Assert.That(ex).IsNull();
        await AdapterHubCallbacks.Received(1)
            .AdapterConfigurationUpdatedAsync(TenantId, Arg.Any<AdapterConfigurationDto>());
        await CommunicationEventService.DidNotReceive().StoreWarningEventAsync(TenantId,
            Arg.Is<string>(m => m.Contains("target tenant")), Arg.Any<RtEntityId>());
    }
}
