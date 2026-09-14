using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

/// <summary>
/// AB#5145: the tenant's System.Communication/SignalChannel singleton (AB#5143 self-service) is
/// ALWAYS projected into every pipeline's configuration list — no Uses association required — so
/// the Signal nodes resolve number/apiUrl from the platform entity instead of app-level
/// SignalImportSettings. Pins: exactly-once projection under the "signal-channel" key, the
/// no-channel no-op, the ship-in-every-state contract (the adapter warns on a non-Registered
/// channel, so it must SEE it), duplicate suppression for a legacy Uses edge, and that a
/// repository failure never fails the deploy.
/// </summary>
internal class SignalChannelProjectionTests : AdapterServiceTestsBase
{
    private const string PipelineDefinition =
        """
        triggers:
          - type: FromSignal@1
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
    public async Task TenantHasSignalChannel_ItIsProjectedExactlyOnceUnderItsWellKnownName()
    {
        var channel = RtEntityCreator.CreateSignalChannel();
        CommunicationRepository.GetSignalChannelsAsync(TenantId).Returns([channel]);
        var (adapter, pipeline) = ArrangeDeployablePipeline();

        var pipelineConfig = await DeployAndCaptureAsync(adapter, pipeline);

        using var _ = Assert.Multiple();
        await Assert.That(pipelineConfig.Configurations.Count(c =>
            c.ConfigurationRtId == channel.RtId)).IsEqualTo(1);
        await Assert.That(pipelineConfig.Configurations.Count(c =>
            c.ConfigurationName == "signal-channel")).IsEqualTo(1);
    }

    [Test]
    public async Task NoSignalChannel_NothingIsAddedAndTheDeploySucceeds()
    {
        // The base stubs GetSignalChannelsAsync to an empty list — the guard path.
        var (adapter, pipeline) = ArrangeDeployablePipeline();

        var pipelineConfig = await DeployAndCaptureAsync(adapter, pipeline);

        await Assert.That(pipelineConfig.Configurations.Any(c =>
            c.ConfigurationName == "signal-channel")).IsFalse();
    }

    [Test]
    public async Task UnregisteredChannel_IsStillProjected_TheAdapterDecidesUsability()
    {
        // Ship-in-every-state contract: only the shipped state lets the node warn "present but
        // not Registered" instead of silently reading nothing.
        var channel = RtEntityCreator.CreateSignalChannel(RtSignalRegistrationStateEnum.CodePending);
        CommunicationRepository.GetSignalChannelsAsync(TenantId).Returns([channel]);
        var (adapter, pipeline) = ArrangeDeployablePipeline();

        var pipelineConfig = await DeployAndCaptureAsync(adapter, pipeline);

        var projected = pipelineConfig.Configurations.Single(c => c.ConfigurationRtId == channel.RtId);
        // The serialized entity carries the state — nested attributes object, enum as number.
        await Assert.That(projected.ConfigurationValue).Contains("\"RegistrationState\":1");
    }

    [Test]
    public async Task ChannelAlreadyLinkedThroughUses_IsNotAddedTwice()
    {
        // A legacy pipeline may still carry a Uses edge to the channel — the well-known name is
        // the adapter-side dictionary key and a duplicate would throw there.
        var channel = RtEntityCreator.CreateSignalChannel();
        CommunicationRepository.GetSignalChannelsAsync(TenantId).Returns([channel]);
        var (adapter, pipeline) = ArrangeDeployablePipeline(channel);

        var pipelineConfig = await DeployAndCaptureAsync(adapter, pipeline);

        await Assert.That(pipelineConfig.Configurations.Count(c =>
            c.ConfigurationName == "signal-channel")).IsEqualTo(1);
    }

    [Test]
    public async Task RepositoryFailure_IsSwallowedAndTheDeployStillSucceeds()
    {
        // A tenant whose CK model predates 3.34.0 has no SignalChannel type — the read throws.
        // The projection is optional enrichment and must never fail the deploy.
        CommunicationRepository.GetSignalChannelsAsync(TenantId)
            .Returns<IReadOnlyCollection<RtSignalChannel>>(_ =>
                throw CommunicationRepositoryException.CommonFailedGettingSignalChannels(TenantId,
                    new InvalidOperationException("CK type not found")));
        var (adapter, pipeline) = ArrangeDeployablePipeline();

        var pipelineConfig = await DeployAndCaptureAsync(adapter, pipeline);

        await Assert.That(pipelineConfig.Configurations.Any(c =>
            c.ConfigurationName == "signal-channel")).IsFalse();
    }

    [Test]
    public async Task TwoChannels_TheLowestRtIdWinsDeterministically()
    {
        // The service layer enforces the singleton; a hand-crafted second definition must not make
        // pods disagree — the tie is broken by ordinal rtId order.
        var first = RtEntityCreator.CreateSignalChannel(id: "aa0000000000000000000001");
        var second = RtEntityCreator.CreateSignalChannel(id: "aa0000000000000000000002");
        CommunicationRepository.GetSignalChannelsAsync(TenantId).Returns([second, first]);
        var (adapter, pipeline) = ArrangeDeployablePipeline();

        var pipelineConfig = await DeployAndCaptureAsync(adapter, pipeline);

        var projected = pipelineConfig.Configurations.Where(c => c.ConfigurationName == "signal-channel").ToList();
        using var _ = Assert.Multiple();
        await Assert.That(projected.Count).IsEqualTo(1);
        await Assert.That(projected.Single().ConfigurationRtId).IsEqualTo(first.RtId);
    }
}
