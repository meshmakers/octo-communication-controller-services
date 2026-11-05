using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

/// <summary>
/// AB#7246: the configuration list shipped to an adapter never contains a System.Communication/SecretDefault.
/// The list reaches the pipeline with Secret values revealed, so the platform-only type is excluded by type,
/// whatever brought it into the list. The value is a sentinel and must not appear anywhere in the shipped data.
/// </summary>
internal class SecretDefaultExclusionTests : AdapterServiceTestsBase
{
    private const string SentinelValue = "sentinel-secret-default-value-7246";

    private const string PipelineDefinition =
        """
        triggers:
          - type: FromHttpRequest@1
        """;

    private static RtConfiguration CreateSecretDefaultEntity()
    {
        var entity = new RtConfiguration
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = new RtCkId<CkTypeId>("System.Communication/SecretDefault"),
            RtWellKnownName = "captcha-secret"
        };
        entity.SetAttributeRawValue("Value", SecretTestValues.Stored(SentinelValue));
        return entity;
    }

    private (RtAdapter Adapter, RtPipeline Pipeline) Arrange(params RtConfiguration[] pipelineConfigurations)
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

    [Test]
    public async Task Deploy_SecretDefaultInTheConfigurationLoad_IsNotShippedAndTheValueNeverLeaves()
    {
        var secretDefault = CreateSecretDefaultEntity();
        var channel = RtEntityCreator.CreateSignalChannel();
        CommunicationRepository.GetSignalChannelsAsync(TenantId).Returns([channel]);
        var (adapter, pipeline) = Arrange(secretDefault, channel);

        AdapterConfigurationDto? captured = null;
        AdapterHubCallbacks
            .When(x => x.AdapterConfigurationUpdatedAsync(TenantId, Arg.Any<AdapterConfigurationDto>()))
            .Do(callInfo => captured = callInfo.Arg<AdapterConfigurationDto>());

        await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId());

        await Assert.That(captured).IsNotNull();
        var shipped = captured!.Pipelines.Single(p => p.PipelineRtEntityId == pipeline.ToRtEntityId());
        using var _ = Assert.Multiple();
        await Assert.That(shipped.Configurations.Any(c => c.ConfigurationRtId == secretDefault.RtId)).IsFalse();
        await Assert.That(shipped.Configurations.Any(c => c.ConfigurationName == "captcha-secret")).IsFalse();
        await Assert.That(shipped.Configurations.Any(c =>
            c.ConfigurationTypeId.ToString().Contains("SecretDefault", StringComparison.Ordinal))).IsFalse();
        await Assert.That(shipped.Configurations.Any(c =>
            c.ConfigurationValue.Contains(SentinelValue, StringComparison.Ordinal))).IsFalse();
        await Assert.That(shipped.Configurations.Any(c => c.ConfigurationRtId == channel.RtId)).IsTrue();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task GetAdapterConfiguration_SecretDefaultInTheConfigurationLoad_IsNotShipped(bool revealSecrets)
    {
        var secretDefault = CreateSecretDefaultEntity();
        var (adapter, _) = Arrange(secretDefault);

        var configuration = await AdapterService.GetAdapterConfigurationAsync(TenantId, adapter.ToRtEntityId(),
            onlyDeployedPipelines: false, revealSecrets: revealSecrets);

        var shipped = configuration.Pipelines.SelectMany(p => p.Configurations).ToList();
        using var _ = Assert.Multiple();
        await Assert.That(shipped.Any(c => c.ConfigurationRtId == secretDefault.RtId)).IsFalse();
        await Assert.That(shipped.Any(c =>
            c.ConfigurationValue.Contains(SentinelValue, StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task PlatformOnlyConfigurationTypes_RecognisesOnlyTheSecretDefaultType()
    {
        using var _ = Assert.Multiple();
        await Assert.That(PlatformOnlyConfigurationTypes.IsPlatformOnly(
            new RtCkId<CkTypeId>("System.Communication/SecretDefault"))).IsTrue();
        await Assert.That(PlatformOnlyConfigurationTypes.IsPlatformOnly(
            new RtCkId<CkTypeId>("System.Communication/EMailSenderConfiguration"))).IsFalse();
        await Assert.That(PlatformOnlyConfigurationTypes.IsPlatformOnly(
            new RtCkId<CkTypeId>("Other.Model/SecretDefault"))).IsFalse();
        await Assert.That(PlatformOnlyConfigurationTypes.IsPlatformOnly(null)).IsFalse();
    }
}
