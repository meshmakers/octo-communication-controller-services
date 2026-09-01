using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

/// <summary>
/// AB#5027: configurations only reach a pipeline through that pipeline's own Uses edges, so the
/// adapter-wide default service account must be mixed into the pipeline's configuration list
/// controller-side. Pins that it lands exactly once, and that a pipeline with its own override
/// is left untouched.
/// </summary>
internal class PipelineServiceAccountProjectionTests : AdapterServiceTestsBase
{
    private const string PipelineDefinition =
        """
        triggers:
          - type: FromHttpRequest@1
        """;

    private (RtAdapter Adapter, RtPipeline Pipeline) ArrangeDeployablePipeline(
        params RtConfiguration[] pipelineConfigurations)
    {
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline = RtEntityCreator.CreatePipeline(PipelineDefinition);

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            null,
            []
        ));

        CommunicationRepository.GetPipelineAsync(TenantId, rtPipeline.ToRtEntityId()).Returns(rtPipeline);
        CommunicationRepository.GetDataFlowByPipelineAsync(TenantId, rtPipeline.RtId).Returns(rtDataFlow);
        CommunicationRepository.GetAdapterAsync(TenantId, rtAdapter.ToRtEntityId()).Returns(rtAdapter);
        CommunicationRepository.GetWorkloadByRtIdAsync(TenantId, rtAdapter.RtId).Returns(rtAdapter);
        CommunicationRepository.GetPipelinesAsync(TenantId, rtAdapter.ToRtEntityId()).Returns([rtPipeline]);
        CommunicationRepository.GetConfigurationsByPipelineAsync(TenantId, rtPipeline.RtId)
            .Returns(Task.FromResult<IEnumerable<RtConfiguration>>(pipelineConfigurations));

        return (rtAdapter, rtPipeline);
    }

    private async Task<PipelineConfigurationDto> DeployAndCaptureAsync(RtAdapter adapter, RtPipeline pipeline)
    {
        AdapterConfigurationDto? captured = null;
        AdapterHubCallbacks
            .When(x => x.AdapterConfigurationUpdatedAsync(TenantId, Arg.Any<AdapterConfigurationDto>()))
            .Do(callInfo => captured = callInfo.Arg<AdapterConfigurationDto>());

        await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId());

        await Assert.That(captured).IsNotNull();
        return captured!.Pipelines.Single(p => p.PipelineRtEntityId == pipeline.ToRtEntityId());
    }

    [Test]
    public async Task NoOverride_AdapterDefaultIsProjectedIntoPipelineConfigurations()
    {
        var (adapter, pipeline) = ArrangeDeployablePipeline();

        var pipelineConfig = await DeployAndCaptureAsync(adapter, pipeline);

        using var _ = Assert.Multiple();
        await Assert.That(pipelineConfig.Configurations.Count(c =>
            c.ConfigurationRtId == DefaultAdapterServiceAccount.RtId)).IsEqualTo(1);
        await Assert.That(pipelineConfig.Configurations.Count(c =>
            c.ConfigurationName == DefaultAdapterServiceAccount.RtWellKnownName)).IsEqualTo(1);
    }

    [Test]
    public async Task AdapterDefaultAlreadyLinkedToPipeline_IsNotAddedTwice()
    {
        // The very same entity reachable both ways: through the pipeline's Uses edge and as the
        // adapter default. RtWellKnownName is the adapter-side dictionary key — a duplicate throws.
        var (adapter, pipeline) = ArrangeDeployablePipeline(DefaultAdapterServiceAccount);

        var pipelineConfig = await DeployAndCaptureAsync(adapter, pipeline);

        await Assert.That(pipelineConfig.Configurations.Count(c =>
            c.ConfigurationName == DefaultAdapterServiceAccount.RtWellKnownName)).IsEqualTo(1);
    }

    [Test]
    public async Task PipelineOverride_AdapterDefaultIsNotProjected()
    {
        var overrideAccount = RtEntityCreator.CreateServiceAccountConfiguration("pipeline-override");
        var (adapter, pipeline) = ArrangeDeployablePipeline(overrideAccount);

        var pipelineConfig = await DeployAndCaptureAsync(adapter, pipeline);

        using var _ = Assert.Multiple();
        await Assert.That(pipelineConfig.Configurations.Count()).IsEqualTo(1);
        await Assert.That(pipelineConfig.Configurations.Single().ConfigurationRtId).IsEqualTo(overrideAccount.RtId);
    }

    [Test]
    public async Task NonServiceAccountConfigurations_AreKeptAndDefaultIsAdded()
    {
        var other = new RtConfiguration
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCkIds.RtCkConfigurationTypeId,
            RtWellKnownName = "sftp"
        };
        var (adapter, pipeline) = ArrangeDeployablePipeline(other);

        var pipelineConfig = await DeployAndCaptureAsync(adapter, pipeline);

        using var _ = Assert.Multiple();
        await Assert.That(pipelineConfig.Configurations.Count()).IsEqualTo(2);
        await Assert.That(pipelineConfig.Configurations.Any(c => c.ConfigurationName == "sftp")).IsTrue();
        await Assert.That(pipelineConfig.Configurations.Any(c =>
            c.ConfigurationRtId == DefaultAdapterServiceAccount.RtId)).IsTrue();
    }
}
