using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
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
}
