using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

/// <summary>
/// AB#5537 — the pipeline configuration projection reveals Secret attributes: an adapter keeps
/// receiving its credentials (SFTP password, Graph client secret, Grafana admin password, …) as
/// plaintext over its SignalR connection, whether the attribute is still a String or already a
/// Secret holding an enc:v2 envelope.
/// </summary>
internal class SecretAttributeProjectionTests : AdapterServiceTestsBase
{
    private const string PipelineDefinition =
        """
        triggers:
          - type: FromPipelineTriggerEvent@1
        """;

    private (RtAdapter Adapter, RtPipeline Pipeline) ArrangeDeployablePipeline(
        params RtConfiguration[] pipelineConfigurations)
    {
        var (adapter, _, pipeline) = ArrangeDeployableDataFlow(pipelineConfigurations);
        return (adapter, pipeline);
    }

    private (RtAdapter Adapter, RtDataFlow DataFlow, RtPipeline Pipeline) ArrangeDeployableDataFlow(
        params RtConfiguration[] pipelineConfigurations)
    {
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline = RtEntityCreator.CreatePipeline(PipelineDefinition);

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(), null, []));

        CommunicationRepository.GetPipelineAsync(TenantId, rtPipeline.ToRtEntityId()).Returns(rtPipeline);
        CommunicationRepository.GetDataFlowByPipelineAsync(TenantId, rtPipeline.RtId).Returns(rtDataFlow);
        CommunicationRepository.GetAdapterAsync(TenantId, rtAdapter.ToRtEntityId()).Returns(rtAdapter);
        CommunicationRepository.GetWorkloadByRtIdAsync(TenantId, rtAdapter.RtId).Returns(rtAdapter);
        CommunicationRepository.GetAdapterByPipelineAsync(TenantId, rtPipeline.ToRtEntityId()).Returns(rtAdapter);
        CommunicationRepository.GetPipelinesAsync(TenantId, rtAdapter.ToRtEntityId()).Returns([rtPipeline]);
        CommunicationRepository.GetPipelinesAsync(TenantId, rtDataFlow.RtId).Returns([rtPipeline]);
        CommunicationRepository.GetConfigurationsByPipelineAsync(TenantId, rtPipeline.RtId)
            .Returns(Task.FromResult<IEnumerable<RtConfiguration>>(pipelineConfigurations));

        return (rtAdapter, rtDataFlow, rtPipeline);
    }

    private RtConfiguration CreateProtectedGrafanaConfiguration(string adminPassword)
    {
        var grafana = CreateGrafanaConfiguration();
        grafana.SetAttributeRawValue(nameof(RtGrafanaConfiguration.AdminPassword),
            SecretEncryption.Protector.Protect(adminPassword));
        return grafana;
    }

    private Func<AdapterConfigurationDto?> CaptureConfigurationSentToAdapter()
    {
        AdapterConfigurationDto? captured = null;
        AdapterHubCallbacks
            .When(x => x.AdapterConfigurationUpdatedAsync(TenantId, Arg.Any<AdapterConfigurationDto>()))
            .Do(callInfo => captured = callInfo.Arg<AdapterConfigurationDto>());
        return () => captured;
    }

    private static RtConfiguration CreateGrafanaConfiguration()
    {
        var configuration = new RtConfiguration
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkGrafanaConfigurationTypeId,
            RtWellKnownName = "grafana"
        };
        configuration.SetAttributeValue(nameof(RtGrafanaConfiguration.GrafanaUrl), AttributeValueTypesDto.String,
            "https://grafana.example.com");
        configuration.SetAttributeValue(nameof(RtGrafanaConfiguration.AdminUser), AttributeValueTypesDto.String,
            "admin");
        return configuration;
    }

    [Test]
    public async Task GetAdapterConfiguration_SecretAttribute_ReachesTheAdapterAsPlaintext()
    {
        // Grafana provisioning runs in the mesh adapter (GrafanaProvisionTenantNode) and reads
        // AdminPassword from exactly this projection.
        const string adminPassword = "grafana-admin-pw";
        var grafana = CreateGrafanaConfiguration();
        grafana.SetAttributeRawValue(nameof(RtGrafanaConfiguration.AdminPassword),
            SecretEncryption.Protector.Protect(adminPassword));
        var (adapter, _) = ArrangeDeployablePipeline(grafana);

        var configuration = await AdapterService.GetAdapterConfigurationAsync(TenantId, adapter.ToRtEntityId(), false,
            revealSecrets: true);

        var projected = configuration.Pipelines.Single().Configurations.Single(c => c.ConfigurationRtId == grafana.RtId);
        using var _ = Assert.Multiple();
        await Assert.That(projected.ConfigurationValue).Contains($"\"AdminPassword\":\"{adminPassword}\"");
        await Assert.That(projected.ConfigurationValue.Contains("isSet", StringComparison.Ordinal)).IsFalse();
        await Assert.That(projected.ConfigurationValue.Contains("enc:v2", StringComparison.Ordinal)).IsFalse();
    }

    /// <summary>
    ///     AB#5537 (4.x lane): the projection a lease carries to the pool member. The member executes the
    ///     pipeline as the borrower's adapter and has nothing to resolve a Secret marker with, so the
    ///     credential must travel revealed — exactly like the adapter-bound pushes above.
    /// </summary>
    [Test]
    public async Task GetLeasedPipelineConfiguration_SecretAttribute_ReachesTheMemberAsPlaintext()
    {
        const string adminPassword = "leased-grafana-admin-pw";
        var grafana = CreateProtectedGrafanaConfiguration(adminPassword);
        var (adapter, pipeline) = ArrangeDeployablePipeline(grafana);

        var projected = await AdapterService.GetLeasedPipelineConfigurationAsync(TenantId, adapter.ToRtEntityId(),
            pipeline.RtId);

        await Assert.That(projected).IsNotNull();
        var configuration = projected!.Configurations.Single(c => c.ConfigurationRtId == grafana.RtId);
        using var _ = Assert.Multiple();
        await Assert.That(configuration.ConfigurationValue).Contains($"\"AdminPassword\":\"{adminPassword}\"");
        await Assert.That(configuration.ConfigurationValue.Contains("isSet", StringComparison.Ordinal)).IsFalse();
        await Assert.That(configuration.ConfigurationValue.Contains("enc:v2", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task GetAdapterConfiguration_StringAttribute_IsShippedUnchanged()
    {
        // Phase 2: before System.Communication 4.8 the credential is a String — same JSON as before.
        var grafana = CreateGrafanaConfiguration();
        grafana.SetAttributeValue(nameof(RtGrafanaConfiguration.AdminPassword), AttributeValueTypesDto.String,
            "plain-admin-pw");
        var (adapter, _) = ArrangeDeployablePipeline(grafana);

        var configuration = await AdapterService.GetAdapterConfigurationAsync(TenantId, adapter.ToRtEntityId(), false,
            revealSecrets: true);

        var projected = configuration.Pipelines.Single().Configurations.Single(c => c.ConfigurationRtId == grafana.RtId);
        await Assert.That(projected.ConfigurationValue).Contains("\"AdminPassword\":\"plain-admin-pw\"");
    }

    [Test]
    public async Task GetAdapterConfiguration_WithoutReveal_KeepsTheMarker()
    {
        // The REST read path (GET {tenantId}/v1/adapter/{id}, read-only policy) must never return
        // plaintext — only the adapter channel reveals.
        const string adminPassword = "grafana-admin-pw";
        var grafana = CreateGrafanaConfiguration();
        grafana.SetAttributeRawValue(nameof(RtGrafanaConfiguration.AdminPassword),
            SecretEncryption.Protector.Protect(adminPassword));
        var (adapter, _) = ArrangeDeployablePipeline(grafana);

        var configuration = await AdapterService.GetAdapterConfigurationAsync(TenantId, adapter.ToRtEntityId(), false);

        var projected = configuration.Pipelines.Single().Configurations.Single(c => c.ConfigurationRtId == grafana.RtId);
        using var _ = Assert.Multiple();
        await Assert.That(projected.ConfigurationValue.Contains(adminPassword, StringComparison.Ordinal)).IsFalse();
        await Assert.That(projected.ConfigurationValue.Contains("enc:v2", StringComparison.Ordinal)).IsFalse();
        await Assert.That(projected.ConfigurationValue).Contains("isSet");
    }

    [Test]
    public async Task RegisterAdapter_RevealsSecretsForTheAdapter()
    {
        const string adminPassword = "grafana-admin-pw";
        var grafana = CreateGrafanaConfiguration();
        grafana.SetAttributeRawValue(nameof(RtGrafanaConfiguration.AdminPassword),
            SecretEncryption.Protector.Protect(adminPassword));
        var (adapter, pipeline) = ArrangeDeployablePipeline(grafana);
        pipeline.DeploymentState = RtDeploymentStateEnum.Deployed;
        AdapterTenant.RemoveAdapter(adapter.ToRtEntityId());

        var configuration = await AdapterService.RegisterAdapterAsync(TenantId, adapter.ToRtEntityId(), ConnectionId);

        var projected = configuration.Pipelines.Single().Configurations.Single(c => c.ConfigurationRtId == grafana.RtId);
        await Assert.That(projected.ConfigurationValue).Contains($"\"AdminPassword\":\"{adminPassword}\"");
    }

    [Test]
    public async Task RegisterAdapter_AlreadyCached_RevealsSecretsForTheAdapter()
    {
        // AB#5827 re-registration of a cached (deaf or reconnecting) adapter goes through the
        // CommitRegistration branch for an existing cache entry — it must reveal like a first one.
        const string adminPassword = "grafana-admin-pw";
        var grafana = CreateProtectedGrafanaConfiguration(adminPassword);
        var (adapter, pipeline) = ArrangeDeployablePipeline(grafana);
        pipeline.DeploymentState = RtDeploymentStateEnum.Deployed;

        var configuration = await AdapterService.RegisterAdapterAsync(TenantId, adapter.ToRtEntityId(), ConnectionId);

        var projected = configuration.Pipelines.Single().Configurations.Single(c => c.ConfigurationRtId == grafana.RtId);
        using var _ = Assert.Multiple();
        await Assert.That(projected.ConfigurationValue).Contains($"\"AdminPassword\":\"{adminPassword}\"");
        await Assert.That(AdapterTenant.AdapterById[adapter.ToRtEntityId()].Configuration.Pipelines.Single()
            .Configurations.Single(c => c.ConfigurationRtId == grafana.RtId).ConfigurationValue).Contains(adminPassword);
    }

    [Test]
    public async Task DeployAdapterConfiguration_PushesRevealedSecretsToTheAdapter()
    {
        // "Update Configuration" push.
        const string adminPassword = "grafana-admin-pw";
        var grafana = CreateProtectedGrafanaConfiguration(adminPassword);
        var (adapter, _) = ArrangeDeployablePipeline(grafana);
        var sent = CaptureConfigurationSentToAdapter();

        await AdapterService.DeployAdapterConfigurationAsync(TenantId, adapter.ToRtEntityId());

        var projected = sent()!.Pipelines.Single().Configurations.Single(c => c.ConfigurationRtId == grafana.RtId);
        await Assert.That(projected.ConfigurationValue).Contains($"\"AdminPassword\":\"{adminPassword}\"");
    }

    [Test]
    public async Task DeployDataFlow_PushesRevealedSecretsToTheAdapter()
    {
        const string adminPassword = "grafana-admin-pw";
        var grafana = CreateProtectedGrafanaConfiguration(adminPassword);
        var (_, dataFlow, _) = ArrangeDeployableDataFlow(grafana);
        var sent = CaptureConfigurationSentToAdapter();

        await AdapterService.DeployDataFlowAsync(TenantId, dataFlow.RtId);

        var projected = sent()!.Pipelines.Single().Configurations.Single(c => c.ConfigurationRtId == grafana.RtId);
        await Assert.That(projected.ConfigurationValue).Contains($"\"AdminPassword\":\"{adminPassword}\"");
    }

    [Test]
    public async Task DeployPipeline_SecretNeverSet_DeploysWithoutTheCredential()
    {
        // AB#5528: the Secret credential slots are optional in 4.8.0 — seeds leave them empty, so a
        // fresh install deploys with the credential not set. The projection must ship the rest of the
        // configuration and no marker; the adapter node reports the missing credential at use time.
        var grafana = CreateGrafanaConfiguration();
        var (adapter, pipeline) = ArrangeDeployablePipeline(grafana);
        var sent = CaptureConfigurationSentToAdapter();

        await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId());

        var projected = sent()!.Pipelines.Single().Configurations.Single(c => c.ConfigurationRtId == grafana.RtId);
        using var _ = Assert.Multiple();
        await Assert.That(projected.ConfigurationValue).Contains("https://grafana.example.com");
        await Assert.That(projected.ConfigurationValue.Contains("isSet", StringComparison.Ordinal)).IsFalse();
        await Assert.That(projected.ConfigurationValue.Contains("\"AdminPassword\":\"", StringComparison.Ordinal))
            .IsFalse();
    }

    [Test]
    public async Task DeployPipeline_UndecryptableSecret_StillDeploysWithTheSecretUnset()
    {
        var grafana = CreateGrafanaConfiguration();
        grafana.SetAttributeRawValue(nameof(RtGrafanaConfiguration.AdminPassword),
            TestSecretEncryption.Create().Protector.Protect("foreign-installation"));
        var (adapter, pipeline) = ArrangeDeployablePipeline(grafana);

        AdapterConfigurationDto? captured = null;
        AdapterHubCallbacks
            .When(x => x.AdapterConfigurationUpdatedAsync(TenantId, Arg.Any<AdapterConfigurationDto>()))
            .Do(callInfo => captured = callInfo.Arg<AdapterConfigurationDto>());

        await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId());

        var projected = captured!.Pipelines.Single().Configurations.Single(c => c.ConfigurationRtId == grafana.RtId);
        using var _ = Assert.Multiple();
        await Assert.That(projected.ConfigurationValue).Contains("\"AdminPassword\":null");
        await Assert.That(projected.ConfigurationValue).Contains("https://grafana.example.com");
    }
}
