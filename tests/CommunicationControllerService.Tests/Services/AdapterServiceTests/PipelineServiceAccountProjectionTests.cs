using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
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

    // ------------------------------------------------------------- AB#5111 issuer token resolution

    [Test]
    public async Task IssuerUriToken_IsResolvedFromTheConfiguredServiceUrls()
    {
        // The reconcile writes {{service.authority}} as the portable default; the projection is the
        // consumption point, so the adapter must receive a concrete URL it can run OIDC discovery
        // against. Wired into the EXISTING deploy-time template machinery (ServiceUrls, the same
        // map {{service.NAME}} resolves from in Hostname/ValueOverrides/ValuesYaml).
        ControllerOptions.ServiceUrls["authority"] = "https://identity.cluster.example.com";
        DefaultAdapterServiceAccount.IssuerUri = "{{service.AUTHORITY}}";
        var (adapter, pipeline) = ArrangeDeployablePipeline();

        var pipelineConfig = await DeployAndCaptureAsync(adapter, pipeline);

        var projected = pipelineConfig.Configurations
            .Single(c => c.ConfigurationRtId == DefaultAdapterServiceAccount.RtId);
        await Assert.That(projected.ConfigurationValue).Contains("https://identity.cluster.example.com");
        await Assert.That(projected.ConfigurationValue.Contains("{{", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task IssuerUriToken_WithoutConfiguredAuthority_FallsBackToTheAuthorityUrl()
    {
        // Local dev / clusters whose chart predates the ServiceUrls map: the token must never
        // resolve to less than the pre-AB#5111 behaviour, which wrote AuthorityUrl verbatim.
        ControllerOptions.AuthorityUrl = "https://identity.fallback.example.com";
        DefaultAdapterServiceAccount.IssuerUri = "{{service.authority}}";
        var (adapter, pipeline) = ArrangeDeployablePipeline();

        var pipelineConfig = await DeployAndCaptureAsync(adapter, pipeline);

        var projected = pipelineConfig.Configurations
            .Single(c => c.ConfigurationRtId == DefaultAdapterServiceAccount.RtId);
        await Assert.That(projected.ConfigurationValue).Contains("https://identity.fallback.example.com");
    }

    [Test]
    public async Task EmptyIssuerUri_IsPassedThroughEmpty()
    {
        // AB#5115: an absent IssuerUri means "the adapter's own installation" and the ADAPTER
        // resolves it — the projection must not substitute anything (no token, no AuthorityUrl).
        ControllerOptions.ServiceUrls["authority"] = "https://identity.cluster.example.com";
        var installationDefault = new RtServiceAccountConfiguration
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkServiceAccountConfigurationTypeId,
            RtWellKnownName = "installation-default-account",
            ClientId = "octo-pipeline-sa-empty",
            ClientSecret = "secret"
            // IssuerUri / TenantId deliberately absent.
        };
        var (adapter, pipeline) = ArrangeDeployablePipeline(installationDefault);

        var pipelineConfig = await DeployAndCaptureAsync(adapter, pipeline);

        var projected = pipelineConfig.Configurations.Single();
        using var _ = Assert.Multiple();
        await Assert.That(projected.ConfigurationValue.Contains("{{", StringComparison.Ordinal)).IsFalse();
        await Assert.That(projected.ConfigurationValue)
            .DoesNotContain("https://identity.cluster.example.com");
        await Assert.That(projected.ConfigurationValue).DoesNotContain(ControllerOptions.AuthorityUrl);
    }

    [Test]
    public async Task ConcreteIssuerUri_IsPassedThroughUntouched()
    {
        // Entities provisioned before AB#5111 (or deliberately pinned by an author) carry a
        // concrete URL — the projection must not rewrite it.
        var (adapter, pipeline) = ArrangeDeployablePipeline();

        var pipelineConfig = await DeployAndCaptureAsync(adapter, pipeline);

        var projected = pipelineConfig.Configurations
            .Single(c => c.ConfigurationRtId == DefaultAdapterServiceAccount.RtId);
        await Assert.That(projected.ConfigurationValue).Contains("https://identity.example.com");
    }

    [Test]
    public async Task PipelineOverrideAccount_GetsItsIssuerTokenResolvedToo()
    {
        // The resolution walks every service account in the projected list, not just the adapter
        // default — a per-pipeline override written by the AB#5111 reconcile carries the token too.
        ControllerOptions.ServiceUrls["authority"] = "https://identity.cluster.example.com";
        var overrideAccount = RtEntityCreator.CreateServiceAccountConfiguration("pipeline-override");
        overrideAccount.IssuerUri = "{{service.authority}}";
        var (adapter, pipeline) = ArrangeDeployablePipeline(overrideAccount);

        var pipelineConfig = await DeployAndCaptureAsync(adapter, pipeline);

        await Assert.That(pipelineConfig.Configurations.Single().ConfigurationValue)
            .Contains("https://identity.cluster.example.com");
    }

    [Test]
    public async Task BaseTypedOverrideFromTheRepository_IsRecognisedAndItsTokenResolved()
    {
        // Production shape: GetConfigurationsByPipelineAsync materialises the Uses targets as the
        // requested base RtConfiguration (generic "RtEntity" discriminator), NOT as the typed
        // RtServiceAccountConfiguration the other tests hand in. The projection must still
        // recognise the override (no adapter default injected) and resolve its issuer token —
        // found live on AB#5111's first delegated run, where an OfType<> test matched neither.
        ControllerOptions.ServiceUrls["authority"] = "https://identity.cluster.example.com";
        var overrideAccount = new RtConfiguration
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkServiceAccountConfigurationTypeId,
            RtWellKnownName = "pipeline-override"
        };
        overrideAccount.SetAttributeValue(nameof(RtServiceAccountConfiguration.ClientId),
            AttributeValueTypesDto.String, "octo-pipeline-sa-base");
        overrideAccount.SetAttributeValue(nameof(RtServiceAccountConfiguration.IssuerUri),
            AttributeValueTypesDto.String, "{{service.authority}}");
        var (adapter, pipeline) = ArrangeDeployablePipeline(overrideAccount);

        var pipelineConfig = await DeployAndCaptureAsync(adapter, pipeline);

        using var _ = Assert.Multiple();
        // Recognised as the override: the adapter default must NOT be projected on top.
        await Assert.That(pipelineConfig.Configurations.Count()).IsEqualTo(1);
        var projected = pipelineConfig.Configurations.Single();
        await Assert.That(projected.ConfigurationRtId).IsEqualTo(overrideAccount.RtId);
        await Assert.That(projected.ConfigurationValue).Contains("https://identity.cluster.example.com");
        await Assert.That(projected.ConfigurationValue.Contains("{{", StringComparison.Ordinal)).IsFalse();
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
