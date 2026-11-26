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
/// Identity=System and a target tenant is refused. Decision 2: a node with Identity=ServiceAccount names its target
/// tenant only statically (tenantId/tenantIds); a target tenant taken from data (tenantIdPath) is refused, whatever the
/// path reads. A Caller node with a data-driven target deploys with a warning. The rules hold for an authorized
/// deployer and with the elevation check switched off.
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

    private const string CronTrigger =
        """
        triggers:
          - type: FromPipelineTriggerEvent@1
        """;

    // Decision 2 (fail closed, no path classification): a service-account node takes no target tenant from data,
    // whatever the path reads and whichever trigger starts the pipeline.
    [Test]
    [Arguments("$.body.recipientTenantId")]
    [Arguments("$.formData.t")]
    [Arguments("body.t")]
    [Arguments("$..t")]
    public async Task ServiceAccountWithTenantIdPath_IsRefused(string path)
    {
        var ex = await DeployAsync(Definition(VerifiedHttpTrigger, "identity: ServiceAccount", path));

        using var _ = Assert.Multiple();
        await Assert.That(ex).IsTypeOf<AdapterServiceException>();
        await Assert.That(ex!.Message).Contains("AB#9767");
        await Assert.That(ex.Message).Contains("only statically");
        await Assert.That(ex.Message).Contains(path);
        await Assert.That(ex.Message).Contains("ApplyChanges@2[0] (ServiceAccount)");
        await AssertNothingDeployed();
    }

    [Test]
    public async Task ServiceAccountWithTenantIdPath_OnACronPipeline_IsRefused_AlsoWithTheElevationCheckOff()
    {
        GuardOptions.CheckElevation = false;

        var ex = await DeployAsync(Definition(CronTrigger, "identity: ServiceAccount", "$.t"));

        using var _ = Assert.Multiple();
        await Assert.That(ex).IsTypeOf<AdapterServiceException>();
        await Assert.That(ex!.Message).Contains("only statically");
        await AssertNothingDeployed();
    }

    [Test]
    public async Task ServiceAccountWithTenantIdPath_InsideForEach_IsRefused()
    {
        var ex = await DeployAsync(CronTrigger + "\n" +
                                   """
                                   transformations:
                                     - type: ForEach@1
                                       iterationPath: $.items
                                       transformations:
                                         - type: ApplyChanges@2
                                           identity: ServiceAccount
                                           entityUpdatesPath: $.updates
                                           targetTenant:
                                             relation: Sibling
                                             tenantIdPath: $.item.tenant
                                   """);

        using var _ = Assert.Multiple();
        await Assert.That(ex).IsTypeOf<AdapterServiceException>();
        await Assert.That(ex!.Message).Contains("$.item.tenant");
        await AssertNothingDeployed();
    }

    [Test]
    public async Task ServiceAccountWithStaticTenantId_Deploys_WithoutWarning()
    {
        var ex = await DeployAsync(CronTrigger + "\n" +
                                   """
                                   transformations:
                                     - type: ApplyChanges@2
                                       identity: ServiceAccount
                                       entityUpdatesPath: $.updates
                                       targetTenant:
                                         relation: Sibling
                                         tenantId: companyb
                                   """);

        using var _ = Assert.Multiple();
        await Assert.That(ex).IsNull();
        await AdapterHubCallbacks.Received(1)
            .AdapterConfigurationUpdatedAsync(TenantId, Arg.Any<AdapterConfigurationDto>());
        await CommunicationEventService.DidNotReceive().StoreWarningEventAsync(TenantId,
            Arg.Is<string>(m => m.Contains("target tenant")), Arg.Any<RtEntityId>());
    }

    // Decision 2, Caller: deploys, also with an anonymous trigger; the mesh adapter refuses a data-driven target at run
    // time unless the caller is verified, and proves the caller's own right in the target. Reported as a warning.
    [Test]
    [Arguments("identity: Caller", "$.body.recipientTenantId")]
    [Arguments("", "$.query.tenant")]
    public async Task CallerWithTenantIdPath_Deploys_WithAWarning(string identity, string path)
    {
        var ex = await DeployAsync(Definition(AnonymousHttpTrigger, identity, path));

        using var _ = Assert.Multiple();
        await Assert.That(ex).IsNull();
        await AdapterHubCallbacks.Received(1)
            .AdapterConfigurationUpdatedAsync(TenantId, Arg.Any<AdapterConfigurationDto>());
        await CommunicationEventService.Received(1).StoreWarningEventAsync(TenantId,
            Arg.Is<string>(m => m.Contains(path) && m.Contains("target tenant")),
            Arg.Any<RtEntityId>());
    }

    // Review finding 1: the adapter reads 'Caller, ServiceAccount' as ServiceAccount (YamlDotNet Enum.Parse); the
    // controller must not read it as Caller.
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
    public async Task ServiceAccountInASwitchCase_WithTenantIdPath_IsRefused()
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

    // A Switch@1 inside a Switch@1 (in a case and in the default branch): the rules walk every level.
    [Test]
    [Arguments("cases")]
    [Arguments("default")]
    public async Task ServiceAccountInASwitchNestedInASwitch_WithTenantIdPath_IsRefused(string branch)
    {
        var inner = branch == "cases"
            ? """
                          cases:
                            - value: x
                              transformations:
                                - type: ApplyChanges@2
                                  identity: ServiceAccount
                                  entityUpdatesPath: $.updates
                                  targetTenant:
                                    relation: Sibling
                                    tenantIdPath: $.body.nested
              """
            : """
                          cases:
                            - value: x
                              transformations: []
                          default:
                            - type: ApplyChanges@2
                              identity: ServiceAccount
                              entityUpdatesPath: $.updates
                              targetTenant:
                                relation: Sibling
                                tenantIdPath: $.body.nested
              """;
        var ex = await DeployAsync(VerifiedHttpTrigger + "\n" +
                                   """
                                   transformations:
                                     - type: Switch@1
                                       path: $.body.kind
                                       cases:
                                         - value: a
                                           transformations:
                                             - type: Switch@1
                                               path: $.body.sub

                                   """ + inner);

        using var _ = Assert.Multiple();
        await Assert.That(ex).IsTypeOf<AdapterServiceException>();
        await Assert.That(ex!.Message).Contains("$.body.nested");
        await Assert.That(ex.Message).Contains("only statically");
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
    public async Task DeployDataFlow_ServiceAccountWithTenantIdPath_IsRefused()
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

    // The REST read of an adapter (GET adapter/{id}) projects without revealing secrets: it is a read, so it leaves the
    // offending pipeline out like a push but writes no deployment state and no event, however often it is called.
    [Test]
    public async Task RestRead_LeavesTheOffendingPipelineOut_AndWritesNothing()
    {
        var (adapter, _, clean) = ArrangeStoredPipelines();

        var first = await AdapterService.GetAdapterConfigurationAsync(TenantId, adapter.ToRtEntityId(), false);
        var second = await AdapterService.GetAdapterConfigurationAsync(TenantId, adapter.ToRtEntityId(), false);

        using var _ = Assert.Multiple();
        foreach (var configuration in new[] { first, second })
        {
            await Assert.That(configuration.Pipelines.Select(p => p.PipelineRtEntityId))
                .IsEquivalentTo([clean.ToRtEntityId()]);
        }

        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetPipelineDeploymentStateAsync(default!, default!, default, default);
        await CommunicationEventService.DidNotReceiveWithAnyArgs()
            .StoreErrorEventAsync(default!, default!, default!);
        await AssertNothingDeployed();
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
