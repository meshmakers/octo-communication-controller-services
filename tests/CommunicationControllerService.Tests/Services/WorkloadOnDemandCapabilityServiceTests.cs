using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

/// <summary>
/// Pins the AB#4984 on-demand capability classification: a workload is OnDemandCapable iff
/// none of its pipelines uses a process-bound trigger, sourced from the known-name fallback
/// list (older SDKs) and the RequiresRunningProcess descriptor flag (self-description).
/// </summary>
internal class WorkloadOnDemandCapabilityServiceTests
{
    private const string TenantId = "tenantId";
    private const string ConnectionId = "connectionId";

    private readonly ICommunicationRepository _repository = Substitute.For<ICommunicationRepository>();
    private readonly IAdapterCache _adapterCache = Substitute.For<IAdapterCache>();
    private readonly WorkloadOnDemandCapabilityService _service;
    private readonly RtAdapter _rtAdapter;

    public WorkloadOnDemandCapabilityServiceTests()
    {
        // Real parser (pure, dependency-free) so classification runs against real YAML
        _service = new WorkloadOnDemandCapabilityService(_repository, _adapterCache,
            new PipelineDefinitionService());
        _rtAdapter = RtEntityCreator.CreateAdapter();
    }

    private void GivenPipelines(params RtPipeline[] pipelines)
    {
        _repository.GetPipelinesAsync(TenantId, _rtAdapter.ToRtEntityId())
            .Returns(pipelines);
    }

    private static RtPipeline CreatePipeline(string name, string pipelineDefinition)
    {
        var pipeline = RtEntityCreator.CreatePipeline();
        pipeline.Name = name;
        pipeline.PipelineDefinition = pipelineDefinition;
        return pipeline;
    }

    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    private void GivenRegisteredAdapterWithDescriptors(params NodeDescriptorDto[] nodeDescriptors)
    {
        var adapterTenant = new AdapterTenant(Substitute.For<IAdapterCachePublish>(), TenantId);
        var adapter = adapterTenant.AddAdapter(_rtAdapter.ToRtEntityId(), ConnectionId,
            new AdapterConfigurationDto(_rtAdapter.ToRtEntityId(), null, []));
        adapter.SetNodeDescriptors(nodeDescriptors);

        _adapterCache.TryGetTenant(TenantId, out Arg.Any<AdapterTenant?>())
            .Returns(x =>
            {
                x[1] = adapterTenant;
                return true;
            });
    }

    [Test]
    public async Task EvaluateAsync_NoPipelines_IsCapable()
    {
        GivenPipelines();

        var result = await _service.EvaluateAsync(TenantId, _rtAdapter.ToRtEntityId());

        using var _ = Assert.Multiple();
        await Assert.That(result.IsCapable).IsTrue();
        await Assert.That(result.BlockingReasons).IsEmpty();
    }

    [Test]
    public async Task EvaluateAsync_WakeCapableTriggersOnly_IsCapable()
    {
        GivenPipelines(CreatePipeline("api",
            """
            triggers:
              - type: FromHttpRequest@1
              - type: FromPipelineTriggerEvent@1
              - type: FromPipelineDataEvent@1
              - type: FromExecutePipelineCommand@1
            """));

        var result = await _service.EvaluateAsync(TenantId, _rtAdapter.ToRtEntityId());

        await Assert.That(result.IsCapable).IsTrue();
    }

    [Test]
    public async Task EvaluateAsync_KnownProcessBoundTrigger_NotCapableWithReason()
    {
        GivenPipelines(CreatePipeline("sync-transactions",
            """
            triggers:
              - type: FromPolling@1
            """));

        var result = await _service.EvaluateAsync(TenantId, _rtAdapter.ToRtEntityId());

        using var _ = Assert.Multiple();
        await Assert.That(result.IsCapable).IsFalse();
        await Assert.That(result.BlockingReasons.Count).IsEqualTo(1);
        await Assert.That(result.BlockingReasons[0]).Contains("sync-transactions");
        await Assert.That(result.BlockingReasons[0]).Contains("FromPolling@1");
    }

    [Test]
    public async Task EvaluateAsync_KnownProcessBoundTrigger_CasingVariant_NotCapable()
    {
        GivenPipelines(CreatePipeline("mail",
            """
            triggers:
              - type: fromMicrosoftGraphEmail@1
            """));

        var result = await _service.EvaluateAsync(TenantId, _rtAdapter.ToRtEntityId());

        await Assert.That(result.IsCapable).IsFalse();
    }

    [Test]
    public async Task EvaluateAsync_DescriptorFlaggedTrigger_NotCapable()
    {
        // A third-party trigger unknown to the fallback list, self-described as
        // process-bound via the RequiresRunningProcess descriptor flag (new SDKs).
        GivenPipelines(CreatePipeline("custom",
            """
            triggers:
              - type: FromCustomBus@1
            """));
        GivenRegisteredAdapterWithDescriptors(new NodeDescriptorDto(
            "FromCustomBus", 1, "Trigger", true, false, "{}", RequiresRunningProcess: true));

        var result = await _service.EvaluateAsync(TenantId, _rtAdapter.ToRtEntityId());

        using var _ = Assert.Multiple();
        await Assert.That(result.IsCapable).IsFalse();
        await Assert.That(result.BlockingReasons[0]).Contains("FromCustomBus@1");
    }

    [Test]
    public async Task EvaluateAsync_UnknownTriggerWithoutFlag_IsCapable()
    {
        // Unknown trigger, not flagged: classified wake-capable. Old SDKs that cannot
        // send the flag are covered by the known-name fallback list for first-party nodes.
        GivenPipelines(CreatePipeline("custom",
            """
            triggers:
              - type: FromCustomBus@1
            """));
        GivenRegisteredAdapterWithDescriptors(new NodeDescriptorDto(
            "FromCustomBus", 1, "Trigger", true, false, "{}"));

        var result = await _service.EvaluateAsync(TenantId, _rtAdapter.ToRtEntityId());

        await Assert.That(result.IsCapable).IsTrue();
    }

    [Test]
    public async Task EvaluateAsync_MultiplePipelines_AllReasonsListed()
    {
        GivenPipelines(
            CreatePipeline("poll", """
                triggers:
                  - type: FromPolling@1
                """),
            CreatePipeline("watch", """
                triggers:
                  - type: FromWatchRtEntity@1
                """),
            CreatePipeline("api", """
                triggers:
                  - type: FromHttpRequest@1
                """));

        var result = await _service.EvaluateAsync(TenantId, _rtAdapter.ToRtEntityId());

        using var _ = Assert.Multiple();
        await Assert.That(result.IsCapable).IsFalse();
        await Assert.That(result.BlockingReasons.Count).IsEqualTo(2);
    }

    [Test]
    public async Task RefreshWorkloadCapabilityAsync_PersistsResult()
    {
        GivenPipelines(CreatePipeline("poll",
            """
            triggers:
              - type: FromPolling@1
            """));

        await _service.RefreshWorkloadCapabilityAsync(TenantId, _rtAdapter.ToRtEntityId());

        await _repository.Received(1).SetWorkloadOnDemandCapabilityAsync(TenantId, _rtAdapter.RtId,
            false, Arg.Is<string?>(r => r != null && r.Contains("FromPolling@1")));
    }

    [Test]
    public async Task RefreshWorkloadCapabilityAsync_CapableWorkload_PersistsNullReasons()
    {
        GivenPipelines();

        await _service.RefreshWorkloadCapabilityAsync(TenantId, _rtAdapter.ToRtEntityId());

        await _repository.Received(1).SetWorkloadOnDemandCapabilityAsync(TenantId, _rtAdapter.RtId,
            true, null);
    }

    [Test]
    public async Task RefreshWorkloadCapabilityAsync_RepositoryThrows_DoesNotThrow()
    {
        // Best-effort contract: the persisted value is a Studio display aid only
        GivenPipelines();
        _repository.SetWorkloadOnDemandCapabilityAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(),
                Arg.Any<bool>(), Arg.Any<string?>())
            .Returns<Task>(_ => throw new InvalidOperationException("db down"));

        await _service.RefreshWorkloadCapabilityAsync(TenantId, _rtAdapter.ToRtEntityId());
    }

    #region AB#5228 — adapter-trigger audit (both classification paths)

    // The triggers the AB#5228 audit reclassified as process-bound, with the repo they live in.
    // Every one of them now carries [NodeRequiresRunningProcess] at the source AND a base name in
    // KnownProcessBoundTriggerNames; the two tests below pin one classification path each.
    //
    //   LoxonePollTrigger@1        octo-adapter-loxone — in-process poll loop (the reported defect)
    //   FromLoxoneStateChange@1    octo-adapter-loxone — WebSocket state-cache subscription
    //   MqttTrigger@1              octo-adapter-mqtt   — long-lived MQTT client subscription
    //   DemoTrigger@1              octo-adapter-mqtt + octo-adapter-demos — TCP listener
    //   FromZenonCel@1             octo-plug-zenon     — in-process runtime subscription
    //   FromZenonVariableChanged@1 octo-plug-zenon     — in-process runtime subscription
    //   FromZenonAml@1             octo-plug-zenon     — in-process runtime subscription
    //   FromRfcServerCall@1        octo-adapter-sap    — RFC server hosted in the process

    /// <summary>
    /// Old-SDK path: the adapter sends no descriptors at all, so only the name fallback can
    /// classify the trigger. Dropping a name from <c>KnownProcessBoundTriggerNames</c> fails here.
    /// </summary>
    [Test]
    [Arguments("LoxonePollTrigger@1")]
    [Arguments("FromLoxoneStateChange@1")]
    [Arguments("MqttTrigger@1")]
    [Arguments("DemoTrigger@1")]
    [Arguments("FromZenonCel@1")]
    [Arguments("FromZenonVariableChanged@1")]
    [Arguments("FromZenonAml@1")]
    [Arguments("FromRfcServerCall@1")]
    public async Task GetProcessBoundNodes_FallbackNameList_NoDescriptors_ClassifiesProcessBound(
        string nodeType)
    {
        var definition = $"""
                          triggers:
                            - type: {nodeType}
                          """;

        var processBound = _service.GetProcessBoundNodes(definition, null);

        await Assert.That(processBound).Contains(nodeType);
    }

    /// <summary>
    /// New-SDK path: the node is spelled with an "X" prefix so no fallback name can answer, and the
    /// descriptor alone carries <c>RequiresRunningProcess</c>. Removing
    /// <c>[NodeRequiresRunningProcess]</c> at the source is what makes an adapter stop sending this
    /// flag — which is exactly the state this test describes when it goes red.
    /// </summary>
    [Test]
    [Arguments("XLoxonePollTrigger")]
    [Arguments("XFromLoxoneStateChange")]
    [Arguments("XMqttTrigger")]
    [Arguments("XDemoTrigger")]
    [Arguments("XFromZenonCel")]
    [Arguments("XFromZenonVariableChanged")]
    [Arguments("XFromZenonAml")]
    [Arguments("XFromRfcServerCall")]
    public async Task GetProcessBoundNodes_DescriptorFlag_WithoutFallbackName_ClassifiesProcessBound(
        string nodeName)
    {
        var nodeType = $"{nodeName}@1";
        var definition = $"""
                          triggers:
                            - type: {nodeType}
                          """;
        var descriptors = new[]
        {
            new NodeDescriptorDto(nodeName, 1, "Trigger", true, false, "{}",
                RequiresRunningProcess: true)
        };

        // Sanity: without the descriptor flag the same node is classified capable, so the assertion
        // below can only be satisfied by the descriptor path.
        await Assert.That(_service.GetProcessBoundNodes(definition, null)).IsEmpty();

        var processBound = _service.GetProcessBoundNodes(definition, descriptors);

        await Assert.That(processBound).Contains(nodeType);
    }

    /// <summary>
    /// Negative case — the audit must not have made everything process-bound. These five are
    /// wake-capable by evidence in the code: the HTTP activator holds the request through the wake
    /// (AB#4923), the cron trigger queue is durable and the controller co-wakes on the same cron
    /// (AB#4918), the execute-pipeline send is wake-gated in
    /// <c>TriggerManagementService.StartExecutePipelineAsync</c>, and chaining stays classified
    /// capable by design (see the doc note in this test's AB#5228 report).
    /// </summary>
    [Test]
    [Arguments("FromHttpRequest@1")]
    [Arguments("FromHttpRequest@2")]
    [Arguments("FromPipelineTriggerEvent@1")]
    [Arguments("FromExecutePipelineCommand@1")]
    [Arguments("FromPipelineDataEvent@1")]
    public async Task GetProcessBoundNodes_WakeCapableTrigger_StaysCapable(string nodeType)
    {
        var definition = $"""
                          triggers:
                            - type: {nodeType}
                          """;

        var processBound = _service.GetProcessBoundNodes(definition, null);

        await Assert.That(processBound).IsEmpty();
    }

    /// <summary>
    /// The reported defect, end to end: a pipeline on LoxonePollTrigger@1 must make its workload
    /// not on-demand capable, with a reason a human can read.
    /// </summary>
    [Test]
    public async Task EvaluateAsync_LoxonePollTrigger_NotCapableWithReason()
    {
        GivenPipelines(CreatePipeline("loxone-poll",
            """
            triggers:
              - type: LoxonePollTrigger@1
            """));

        var result = await _service.EvaluateAsync(TenantId, _rtAdapter.ToRtEntityId());

        using var _ = Assert.Multiple();
        await Assert.That(result.IsCapable).IsFalse();
        await Assert.That(result.BlockingReasons.Count).IsEqualTo(1);
        await Assert.That(result.BlockingReasons[0]).Contains("loxone-poll");
        await Assert.That(result.BlockingReasons[0]).Contains("LoxonePollTrigger@1");
    }

    #endregion
}
