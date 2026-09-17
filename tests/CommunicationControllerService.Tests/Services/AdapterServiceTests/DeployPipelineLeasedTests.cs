using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

/// <summary>
///     AB#4924 — deploying a pipeline to an adapter whose <c>LifecycleMode</c> is <c>Leased</c>.
/// </summary>
/// <remarks>
///     <para>
///         The defect these pin: <c>DeployPipelineAsync</c> wrapped its entire body in a lookup of
///         <c>AdapterById</c>, which is only populated for adapters holding an open
///         <c>/{tenantId}/adapterHub</c> connection. A leased adapter never has one — it has no
///         process of its own — so every leased deploy answered 404 and <b>none of the four deploy
///         gates ever ran</b> on that path. The pipeline was saved by other means and then executed
///         by a borrowed process that had never checked its service account or its elevation.
///     </para>
///     <para>
///         🔴 Nothing here calls <c>AdapterTenant.AddAdapter</c>. That is the point: the arrangement
///         a leased adapter has in production is "not in the adapter cache at all", and a test that
///         quietly put it there would pass against the very code that was broken.
///     </para>
/// </remarks>
internal class DeployPipelineLeasedTests : AdapterServiceTestsBase
{
    private const string LenderTenantId = "lender";
    private const string AdapterPoolRtId = "6ad562f3ff7c40ff80275b84";
    private const string MemberConnectionId = "conn-pool-member-1";
    private const string MemberId = "octo-pool-0";

    /// <summary>
    ///     A trigger the controller's <c>KnownInteractiveTriggerNames</c> fallback does NOT know.
    ///     Deliberate: with a name the fallback recognises, a test would come out Interactive whether
    ///     or not the pool's descriptors were consulted, and would prove nothing about part 3.
    /// </summary>
    private const string PoolOnlyInteractiveTrigger = "FromCustomThing@1";

    private const string PoolOnlyInteractiveDefinition =
        """
        triggers:
          - type: FromCustomThing@1
        """;

    private const string ProcessBoundDefinition =
        """
        triggers:
          - type: FromPolling@1
        """;

    private const string ElevatedDefinition =
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

    /// <summary>Key 0 of the <c>PipelineExecutionClass</c> CK enum.</summary>
    private const int Interactive = 0;

    /// <summary>Key 1 — and the CK attribute default, which is what a leased deploy used to keep.</summary>
    private const int Batch = 1;

    private const string PoolPipelineSchemaJson = """{"$id":"pool-schema"}""";

    private static NodeDescriptorDto Descriptor(string name, int version, bool isTrigger, int executionClass,
        bool requiresRunningProcess = false) =>
        new(name, version, isTrigger ? "Trigger" : "Transform", isTrigger, false, "{}",
            false, null, requiresRunningProcess, executionClass);

    /// <summary>
    ///     Registers a member of the LENDING tenant's pool, exactly as
    ///     <c>AdapterPoolHub.RegisterPoolMemberAsync</c> does for a live member.
    /// </summary>
    private void GivenPoolMember(IReadOnlyList<NodeDescriptorDto>? descriptors,
        string? pipelineSchemaJson = PoolPipelineSchemaJson)
    {
        PoolConnectionManager.RegisterMember(MemberConnectionId, MemberId, LenderTenantId, AdapterPoolRtId,
            descriptors, pipelineSchemaJson);
    }

    private (RtAdapter Adapter, RtPipeline Pipeline) ArrangeLeasedPipeline(string? pipelineDefinition,
        string? lentFromTenantId = LenderTenantId, string? lentFromAdapterPoolRtId = AdapterPoolRtId)
    {
        var rtAdapter = RtEntityCreator.CreateAdapter();
        rtAdapter.Name = "borrowed-adapter";
        rtAdapter.LifecycleMode = RtLifecycleModeEnum.Leased;
        rtAdapter.LentFromTenantId = lentFromTenantId;
        rtAdapter.LentFromAdapterPoolRtId = lentFromAdapterPoolRtId;

        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline = RtEntityCreator.CreatePipeline(pipelineDefinition);

        CommunicationRepository.GetPipelineAsync(TenantId, rtPipeline.ToRtEntityId()).Returns(rtPipeline);
        CommunicationRepository.GetDataFlowByPipelineAsync(TenantId, rtPipeline.RtId).Returns(rtDataFlow);
        CommunicationRepository.GetAdapterAsync(TenantId, rtAdapter.ToRtEntityId()).Returns(rtAdapter);
        CommunicationRepository.GetWorkloadByRtIdAsync(TenantId, rtAdapter.RtId).Returns(rtAdapter);
        CommunicationRepository.GetPipelinesAsync(TenantId, rtAdapter.ToRtEntityId()).Returns([rtPipeline]);
        CommunicationRepository.GetConfigurationsByPipelineAsync(TenantId, rtPipeline.RtId)
            .Returns(Task.FromResult<IEnumerable<RtConfiguration>>([]));

        return (rtAdapter, rtPipeline);
    }

    /// <summary>
    ///     The contrast case: a dedicated adapter, arranged the way every pre-existing suite does.
    /// </summary>
    private (RtAdapter Adapter, RtPipeline Pipeline) ArrangeDedicatedPipeline(string? pipelineDefinition)
    {
        var rtAdapter = RtEntityCreator.CreateAdapter();
        rtAdapter.Name = "dedicated-adapter";
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline = RtEntityCreator.CreatePipeline(pipelineDefinition);

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(), null, []));

        CommunicationRepository.GetPipelineAsync(TenantId, rtPipeline.ToRtEntityId()).Returns(rtPipeline);
        CommunicationRepository.GetDataFlowByPipelineAsync(TenantId, rtPipeline.RtId).Returns(rtDataFlow);
        CommunicationRepository.GetAdapterAsync(TenantId, rtAdapter.ToRtEntityId()).Returns(rtAdapter);
        CommunicationRepository.GetWorkloadByRtIdAsync(TenantId, rtAdapter.RtId).Returns(rtAdapter);
        CommunicationRepository.GetPipelinesAsync(TenantId, rtAdapter.ToRtEntityId()).Returns([rtPipeline]);
        CommunicationRepository.GetConfigurationsByPipelineAsync(TenantId, rtPipeline.RtId)
            .Returns(Task.FromResult<IEnumerable<RtConfiguration>>([]));

        return (rtAdapter, rtPipeline);
    }

    // ---------------------------------------------------------------------------------------
    // The headline: it deploys at all, and the class comes from the pool.
    // ---------------------------------------------------------------------------------------

    [Test]
    public async Task DeployPipelineAsync_LeasedAdapter_PersistsTheDefinitionAndThePoolResolvedExecutionClass()
    {
        GivenPoolMember([Descriptor("FromCustomThing", 1, isTrigger: true, executionClass: Interactive)]);
        var (adapter, pipeline) = ArrangeLeasedPipeline(pipelineDefinition: null);

        await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId(),
            PoolOnlyInteractiveDefinition);

        // 🔴 Interactive, not the CK default Batch. The trigger name is unknown to the controller's
        // fallback list, so the ONLY way to reach Interactive is through the pool member's
        // descriptors — which is the whole of part 3.
        await CommunicationRepository.Received(1).SetPipelineDefinitionAsync(TenantId, pipeline.ToRtEntityId(),
            PoolOnlyInteractiveDefinition, Interactive);
    }

    [Test]
    public async Task DeployPipelineAsync_LeasedAdapter_WithNoPoolMemberRegistered_StillDeploysAndFallsBackToBatch()
    {
        // No GivenPoolMember: a controller replica that holds no member of the lending pool. The
        // deploy must still run every gate and persist; only the class degrades.
        var (adapter, pipeline) = ArrangeLeasedPipeline(pipelineDefinition: null);

        await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId(),
            PoolOnlyInteractiveDefinition);

        await CommunicationRepository.Received(1).SetPipelineDefinitionAsync(TenantId, pipeline.ToRtEntityId(),
            PoolOnlyInteractiveDefinition, Batch);
    }

    [Test]
    public async Task DeployPipelineAsync_LeasedAdapter_RedeployWithoutADefinition_ReResolvesTheClassFromThePool()
    {
        GivenPoolMember([Descriptor("FromCustomThing", 1, isTrigger: true, executionClass: Interactive)]);
        var (adapter, pipeline) = ArrangeLeasedPipeline(PoolOnlyInteractiveDefinition);

        await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId());

        using var _ = Assert.Multiple();
        await CommunicationRepository.Received(1)
            .SetPipelineExecutionClassAsync(TenantId, pipeline.ToRtEntityId(), Interactive);
        // A redeploy must not write a definition it was not given (AB#4924, plan §6).
        await CommunicationRepository.DidNotReceiveWithAnyArgs().SetPipelineDefinitionAsync(
            Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<string>(), Arg.Any<int>());
        await CommunicationRepository.Received(1)
            .SyncPipelineDataConnectionsAsync(TenantId, pipeline.ToRtEntityId(), PoolOnlyInteractiveDefinition);
    }

    // ---------------------------------------------------------------------------------------
    // Nothing is pushed — and a dedicated adapter still is.
    // ---------------------------------------------------------------------------------------

    [Test]
    public async Task DeployPipelineAsync_LeasedAdapter_PushesNothing()
    {
        GivenPoolMember([Descriptor("FromCustomThing", 1, isTrigger: true, executionClass: Interactive)]);
        var (adapter, pipeline) = ArrangeLeasedPipeline(pipelineDefinition: null);

        await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId(),
            PoolOnlyInteractiveDefinition);

        using var _ = Assert.Multiple();
        await AdapterHubCallbacks.DidNotReceiveWithAnyArgs()
            .AdapterConfigurationUpdatedAsync(Arg.Any<string>(), Arg.Any<AdapterConfigurationDto>());
        // Pending means "a push is in flight and may still fail". There is no push.
        await CommunicationRepository.DidNotReceive().SetPipelineDeploymentStateAsync(TenantId,
            Arg.Any<RtEntityId>(), RtDeploymentStateEnum.Pending, Arg.Any<string?>());
    }

    [Test]
    public async Task DeployPipelineAsync_DedicatedAdapter_StillPushes()
    {
        var (adapter, pipeline) = ArrangeDedicatedPipeline(pipelineDefinition: null);

        await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId(),
            PoolOnlyInteractiveDefinition);

        // The contrast that makes the assertion above mean something: same call, same fixture,
        // one field different on the adapter, and this one DOES reach the hub.
        await AdapterHubCallbacks.Received(1)
            .AdapterConfigurationUpdatedAsync(TenantId, Arg.Any<AdapterConfigurationDto>());
    }

    [Test]
    public async Task DeployPipelineAsync_LeasedAdapter_MarksThePipelineDeployedAndSaysWhyNothingWasPushed()
    {
        GivenPoolMember([Descriptor("FromCustomThing", 1, isTrigger: true, executionClass: Interactive)]);
        var (adapter, pipeline) = ArrangeLeasedPipeline(pipelineDefinition: null);

        await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId(),
            PoolOnlyInteractiveDefinition);

        await CommunicationRepository.Received(1).SetPipelineDeploymentStateAsync(TenantId, pipeline.ToRtEntityId(),
            RtDeploymentStateEnum.Deployed,
            Arg.Is<string?>(m => m != null && m.Contains("leased") && m.Contains("lease")));
    }

    [Test]
    public async Task DeployPipelineAsync_LeasedAdapter_DoesNotTryToWakeAWorkload()
    {
        GivenPoolMember([Descriptor("FromCustomThing", 1, isTrigger: true, executionClass: Interactive)]);
        var (adapter, pipeline) = ArrangeLeasedPipeline(pipelineDefinition: null);

        await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId(),
            PoolOnlyInteractiveDefinition);

        // A leased adapter has no workload of its own; the pool is the lender's workload and is not
        // a borrower's to wake.
        await WorkloadLifecycleService.DidNotReceiveWithAnyArgs()
            .EnsureWorkloadRunningAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>());
    }

    [Test]
    public async Task DeployPipelineAsync_DedicatedAdapter_StillRunsTheWakeGate()
    {
        var (adapter, pipeline) = ArrangeDedicatedPipeline(pipelineDefinition: null);

        await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId(),
            PoolOnlyInteractiveDefinition);

        await WorkloadLifecycleService.Received(1).EnsureWorkloadRunningAsync(TenantId, adapter.RtId);
    }

    // ---------------------------------------------------------------------------------------
    // The four gates, on the leased path.
    // ---------------------------------------------------------------------------------------

    [Test]
    public async Task DeployPipelineAsync_LeasedAdapter_ElevatedNode_UnauthorizedCaller_IsRejectedBeforeAnyWrite()
    {
        GivenPoolMember([Descriptor("FromHttpRequest", 2, isTrigger: true, executionClass: Interactive)]);
        var (adapter, pipeline) = ArrangeLeasedPipeline(pipelineDefinition: null);
        SetCaller(withUserManagementRole: false);

        var ex = await Assert.ThrowsAsync<Exception>(async () =>
            await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId(),
                ElevatedDefinition));

        using var _ = Assert.Multiple();
        await Assert.That(ex!).IsTypeOf<AdapterServiceException>();
        await Assert.That(ex!.Message).Contains("AB#5128");
        // The gate is worth nothing if it runs after the write.
        await CommunicationRepository.DidNotReceiveWithAnyArgs().SetPipelineDefinitionAsync(
            Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<string>(), Arg.Any<int>());
        await CommunicationRepository.DidNotReceiveWithAnyArgs().SetPipelineDeploymentStateAsync(
            Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<RtDeploymentStateEnum>(), Arg.Any<string?>());
    }

    [Test]
    public async Task DeployPipelineAsync_LeasedAdapter_ElevatedNode_AuthorizedCaller_IsAccepted()
    {
        GivenPoolMember([Descriptor("FromHttpRequest", 2, isTrigger: true, executionClass: Interactive)]);
        var (adapter, pipeline) = ArrangeLeasedPipeline(pipelineDefinition: null);
        SetCaller(withUserManagementRole: true);

        await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId(),
            ElevatedDefinition);

        await CommunicationRepository.Received(1).SetPipelineDefinitionAsync(TenantId, pipeline.ToRtEntityId(),
            ElevatedDefinition, Interactive);
    }

    [Test]
    public async Task DeployPipelineAsync_LeasedAdapter_WithoutAServiceAccount_IsRejected()
    {
        GivenPoolMember([Descriptor("FromCustomThing", 1, isTrigger: true, executionClass: Interactive)]);
        var (adapter, pipeline) = ArrangeLeasedPipeline(pipelineDefinition: null);
        CommunicationRepository.GetServiceAccountForAdapterAsync(TenantId, adapter.RtId)
            .Returns((RtServiceAccountConfiguration?)null);

        var ex = await Assert.ThrowsAsync<Exception>(async () =>
            await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId(),
                PoolOnlyInteractiveDefinition));

        using var _ = Assert.Multiple();
        await Assert.That(ex!).IsTypeOf<AdapterServiceException>();
        await Assert.That(ex!.Message).Contains("AB#5027");
        await CommunicationRepository.DidNotReceiveWithAnyArgs().SetPipelineDefinitionAsync(
            Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<string>(), Arg.Any<int>());
    }

    [Test]
    public async Task DeployPipelineAsync_LeasedAdapter_WithAProcessBoundTrigger_IsRejected()
    {
        GivenPoolMember([Descriptor("FromPolling", 1, isTrigger: true, executionClass: Batch,
            requiresRunningProcess: true)]);
        var (adapter, pipeline) = ArrangeLeasedPipeline(pipelineDefinition: null);

        var ex = await Assert.ThrowsAsync<Exception>(async () =>
            await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId(),
                ProcessBoundDefinition));

        using var _ = Assert.Multiple();
        await Assert.That(ex!).IsTypeOf<AdapterServiceException>();
        await Assert.That(ex!.Message).Contains("FromPolling@1");
        await Assert.That(ex!.Message).Contains("Leased");
        await CommunicationRepository.DidNotReceiveWithAnyArgs().SetPipelineDefinitionAsync(
            Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<string>(), Arg.Any<int>());
    }

    [Test]
    public async Task DeployPipelineAsync_LeasedAdapter_ValidatesTheDefinitionAgainstThePoolsSchema()
    {
        ControllerOptions.EnablePipelineSchemaValidation = true;
        GivenPoolMember([Descriptor("FromCustomThing", 1, isTrigger: true, executionClass: Interactive)]);
        PipelineSchemaValidator.Validate(PoolOnlyInteractiveDefinition, PoolPipelineSchemaJson)
            .Returns(["'FromCustomThing@1' is not a known node"]);
        var (adapter, pipeline) = ArrangeLeasedPipeline(pipelineDefinition: null);

        var ex = await Assert.ThrowsAsync<Exception>(async () =>
            await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId(),
                PoolOnlyInteractiveDefinition));

        using var _ = Assert.Multiple();
        await Assert.That(ex!).IsTypeOf<AdapterServiceException>();
        // The validator was handed the POOL's schema. Before AB#4924 a leased pipeline was validated
        // against no schema at all, so this call never happened.
        PipelineSchemaValidator.Received(1).Validate(PoolOnlyInteractiveDefinition, PoolPipelineSchemaJson);
    }

    // ---------------------------------------------------------------------------------------
    // Where the descriptors must NOT come from.
    // ---------------------------------------------------------------------------------------

    [Test]
    public async Task DeployPipelineAsync_LeasedAdapter_IgnoresAStaleAdapterCacheEntryOfItsOwn()
    {
        // A tenant that switched this adapter from AlwaysOn to Leased can still have a cached entry
        // from the process it used to run. Those descriptors describe a process that no longer
        // exists; honouring them would be wrong-and-confident.
        GivenPoolMember([Descriptor("FromCustomThing", 1, isTrigger: true, executionClass: Batch)]);
        var (adapter, pipeline) = ArrangeLeasedPipeline(pipelineDefinition: null);
        AdapterTenant.AddAdapter(adapter.ToRtEntityId(), ConnectionId,
            new AdapterConfigurationDto(adapter.ToRtEntityId(), null, []));
        AdapterTenant.AdapterById[adapter.ToRtEntityId()].SetNodeDescriptors(
            [Descriptor("FromCustomThing", 1, isTrigger: true, executionClass: Interactive)]);

        await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId(),
            PoolOnlyInteractiveDefinition);

        using var _ = Assert.Multiple();
        // The POOL says Batch; the stale cache says Interactive. The pool wins.
        await CommunicationRepository.Received(1).SetPipelineDefinitionAsync(TenantId, pipeline.ToRtEntityId(),
            PoolOnlyInteractiveDefinition, Batch);
        // And the stale cache entry must not turn the deploy back into a push either.
        await AdapterHubCallbacks.DidNotReceiveWithAnyArgs()
            .AdapterConfigurationUpdatedAsync(Arg.Any<string>(), Arg.Any<AdapterConfigurationDto>());
    }

    [Test]
    public async Task DeployPipelineAsync_LeasedAdapterWithoutALender_DeploysWithTheFallbackClass()
    {
        // LentFrom* unset — a borrower that was authored but never deployed (DeploymentSiteService refuses this
        // at workload deploy; DeployPipeline can still reach it). It must not throw, and it must not
        // silently pick some other pool's descriptors.
        GivenPoolMember([Descriptor("FromCustomThing", 1, isTrigger: true, executionClass: Interactive)]);
        var (adapter, pipeline) = ArrangeLeasedPipeline(pipelineDefinition: null,
            lentFromTenantId: null, lentFromAdapterPoolRtId: null);

        await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId(),
            PoolOnlyInteractiveDefinition);

        await CommunicationRepository.Received(1).SetPipelineDefinitionAsync(TenantId, pipeline.ToRtEntityId(),
            PoolOnlyInteractiveDefinition, Batch);
    }
}
