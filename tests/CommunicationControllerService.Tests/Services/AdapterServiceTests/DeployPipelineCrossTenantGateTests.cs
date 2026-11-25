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

    private (RtAdapter Adapter, RtDataFlow DataFlow, RtPipeline Pipeline) ArrangeDeployablePipeline(
        string pipelineDefinition)
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

        return (rtAdapter, rtDataFlow, rtPipeline);
    }

    private async Task<Exception?> DeployAsync(string definition)
    {
        var (adapter, _, pipeline) = ArrangeDeployablePipeline(definition);
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

    // Review finding 1: the adapter reads 'Caller, ServiceAccount' as ServiceAccount (YamlDotNet Enum.Parse); the
    // controller must not read it as Caller and let the caller steer the service account's tenant.
    [Test]
    [Arguments("'Caller, ServiceAccount'")]
    [Arguments("'01'")]
    public async Task UnrecognisedIdentity_IsRefused(string identity)
    {
        var ex = await DeployAsync(Definition(VerifiedHttpTrigger, $"identity: {identity}", "$.body.t"));

        using var _ = Assert.Multiple();
        await Assert.That(ex).IsTypeOf<AdapterServiceException>();
        await Assert.That(ex!.Message).Contains("AB#9767");
        await Assert.That(ex.Message).Contains("Identity");
        await AssertNothingDeployed();
    }

    // Review finding 2: a node in a Switch@1 case is checked like every other node.
    [Test]
    public async Task ServiceAccountInASwitchCase_WithCallerControlledTenantIdPath_IsRefused()
    {
        var ex = await DeployAsync(VerifiedHttpTrigger + "\n" +
                                   """
                                   transformations:
                                     - type: Switch@1
                                       path: $.body.kind
                                       cases:
                                         - value: a
                                           transformations:
                                             - type: ApplyChanges@2
                                               identity: ServiceAccount
                                               entityUpdatesPath: $.updates
                                               targetTenant:
                                                 relation: Sibling
                                                 tenantIdPath: $.body.t
                                   """);

        using var _ = Assert.Multiple();
        await Assert.That(ex).IsTypeOf<AdapterServiceException>();
        await Assert.That(ex!.Message).Contains("$.body.t");
        await AssertNothingDeployed();
    }

    // Review finding 4: the Teams sender writes $.Emails; a service account must not act in a tenant it names.
    [Test]
    public async Task ServiceAccountWithTeamsBotOutputAsTenantIdPath_IsRefused()
    {
        var ex = await DeployAsync(Definition(
            "triggers:\n  - type: FromTeamsBot@1\n    serverConfiguration: bot",
            "identity: ServiceAccount", "$.Emails[0].Subject"));

        using var _ = Assert.Multiple();
        await Assert.That(ex).IsTypeOf<AdapterServiceException>();
        await Assert.That(ex!.Message).Contains("$.Emails[0].Subject");
        await AssertNothingDeployed();
    }

    [Test]
    public async Task ServiceAccountWithMultiMatchTenantIdPath_IsRefused()
    {
        var ex = await DeployAsync(Definition(VerifiedHttpTrigger, "identity: ServiceAccount", "$..t"));

        await Assert.That(ex).IsTypeOf<AdapterServiceException>();
        await AssertNothingDeployed();
    }

    // Unconfirmed review point: a definition the controller cannot read was let through without any rule (fail open).
    [Test]
    public async Task UnreadableDefinition_IsRefused()
    {
        var ex = await DeployAsync("transformations: [ { type: ApplyChanges@2, identity: ServiceAccount");

        using var _ = Assert.Multiple();
        await Assert.That(ex).IsTypeOf<AdapterServiceException>();
        await Assert.That(ex!.Message).Contains("cannot be read");
        await AssertNothingDeployed();
    }

    // Review finding 5: the data-flow deploy applies the same rules.
    [Test]
    public async Task DeployDataFlow_ServiceAccountWithCallerControlledTenantIdPath_IsRefused()
    {
        var (_, dataFlow, _) =
            ArrangeDeployablePipeline(Definition(VerifiedHttpTrigger, "identity: ServiceAccount", "$.body.t"));
        SetCaller(withUserManagementRole: true);

        var ex = await Assert.That(async () => await AdapterService.DeployDataFlowAsync(TenantId, dataFlow.RtId))
            .Throws<AdapterServiceException>();

        using var _ = Assert.Multiple();
        await Assert.That(ex!.Message).Contains("$.body.t");
        await AssertNothingDeployed();
    }

    // Review finding 3: every push path projects through CreatePipelineConfigurationAsync, which applies the rules to
    // the stored definition. An offending pipeline (imported, seeded by a blueprint, edited in the asset repository)
    // is left out of the push and put into the Error state; the other pipelines still go out.
    private const string CleanDefinition =
        """
        triggers:
          - type: FromPipelineTriggerEvent@1
        transformations:
          - type: ApplyChanges@2
            entityUpdatesPath: $.updates
        """;

    private (RtAdapter Adapter, RtPipeline Offending, RtPipeline Clean) ArrangeStoredPipelines()
    {
        var (adapter, dataFlow, offending) =
            ArrangeDeployablePipeline(Definition(VerifiedHttpTrigger, "identity: ServiceAccount", "$.body.t"));
        var clean = RtEntityCreator.CreatePipeline(CleanDefinition);
        CommunicationRepository.GetPipelinesAsync(TenantId, adapter.ToRtEntityId()).Returns([offending, clean]);
        CommunicationRepository.GetPipelineAsync(TenantId, clean.ToRtEntityId()).Returns(clean);
        CommunicationRepository.GetDataFlowByPipelineAsync(TenantId, clean.RtId).Returns(dataFlow);
        CommunicationRepository.GetConfigurationsByPipelineAsync(TenantId, clean.RtId)
            .Returns(Task.FromResult<IEnumerable<RtConfiguration>>([]));
        return (adapter, offending, clean);
    }

    private async Task AssertSetToError(RtPipeline pipeline) =>
        await CommunicationRepository.Received().SetPipelineDeploymentStateAsync(TenantId, pipeline.ToRtEntityId(),
            RtDeploymentStateEnum.Error,
            Arg.Is<string?>(m => m != null && m.Contains("AB#9767") && m.Contains("$.body.t")));

    [Test]
    public async Task UpdateConfiguration_LeavesTheOffendingPipelineOut_AndSetsItToError()
    {
        var (adapter, offending, clean) = ArrangeStoredPipelines();

        await AdapterService.DeployAdapterConfigurationAsync(TenantId, adapter.ToRtEntityId());

        using var _ = Assert.Multiple();
        await AdapterHubCallbacks.Received(1).AdapterConfigurationUpdatedAsync(TenantId,
            Arg.Is<AdapterConfigurationDto>(c =>
                c.Pipelines.Count == 1 && c.Pipelines.First().PipelineRtEntityId == clean.ToRtEntityId()));
        await AssertSetToError(offending);
        await CommunicationRepository.DidNotReceive().SetPipelineDeploymentStateAsync(TenantId,
            offending.ToRtEntityId(), RtDeploymentStateEnum.Pending, Arg.Any<string?>());
    }

    [Test]
    public async Task Registration_LeavesTheOffendingPipelineOut_AndSetsItToError()
    {
        var (adapter, offending, clean) = ArrangeStoredPipelines();

        var configuration = await AdapterService.GetAdapterConfigurationAsync(TenantId, adapter.ToRtEntityId(), true,
            revealSecrets: true);

        using var _ = Assert.Multiple();
        await Assert.That(configuration.Pipelines.Count).IsEqualTo(1);
        await Assert.That(configuration.Pipelines.First().PipelineRtEntityId).IsEqualTo(clean.ToRtEntityId());
        await AssertSetToError(offending);
    }

    [Test]
    public async Task Lease_OfTheOffendingPipeline_IsRefused_AndSetsItToError()
    {
        var (adapter, offending, clean) = ArrangeStoredPipelines();

        var refused = await AdapterService.GetLeasedPipelineConfigurationAsync(TenantId, adapter.ToRtEntityId(),
            offending.RtId);
        var leased = await AdapterService.GetLeasedPipelineConfigurationAsync(TenantId, adapter.ToRtEntityId(),
            clean.RtId);

        using var _ = Assert.Multiple();
        await Assert.That(refused).IsNull();
        await Assert.That(leased).IsNotNull();
        await AssertSetToError(offending);
    }
}
