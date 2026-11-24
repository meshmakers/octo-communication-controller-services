using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

/// <summary>
/// AB#9776 (decision "own issuer only"): the deploy guard refuses a pipeline whose service account names an
/// identity service other than this installation's, or a tenant other than the pipeline's — the mesh adapter
/// refuses both on every execution. Empty, <c>{{service.authority}}</c> and the own authority keep deploying.
/// </summary>
internal class DeployPipelineServiceAccountOwnIssuerTests : AdapterServiceTestsBase
{
    private const string PipelineDefinition =
        """
        triggers:
          - type: FromHttpRequest@1
        """;

    private (RtAdapter Adapter, RtDataFlow DataFlow, RtPipeline Pipeline) ArrangeDeployablePipeline(
        string? issuerUri, string? tenantId)
    {
        var rtAdapter = RtEntityCreator.CreateAdapter();
        rtAdapter.Name = "mesh-adapter";
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
        CommunicationRepository.GetAdapterByPipelineAsync(TenantId, rtPipeline.ToRtEntityId()).Returns(rtAdapter);
        CommunicationRepository.GetPipelinesAsync(TenantId, rtAdapter.ToRtEntityId()).Returns([rtPipeline]);
        CommunicationRepository.GetPipelinesAsync(TenantId, rtDataFlow.RtId).Returns([rtPipeline]);

        var account = new RtServiceAccountConfiguration
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkServiceAccountConfigurationTypeId,
            RtWellKnownName = "pipeline-account",
            ClientId = "octo-pipeline-sa-pipeline",
            ClientSecret = SecretTestValues.Stored("pipeline-secret")
        };
        if (issuerUri != null)
        {
            account.IssuerUri = issuerUri;
        }

        if (tenantId != null)
        {
            account.TenantId = tenantId;
        }

        CommunicationRepository.GetConfigurationsByPipelineAsync(TenantId, rtPipeline.RtId)
            .Returns(Task.FromResult<IEnumerable<RtConfiguration>>([account]));

        return (rtAdapter, rtDataFlow, rtPipeline);
    }

    [Test]
    [Arguments("https://identity.of-another-installation.example.net")]
    [Arguments("http://identity.example.com")] // scheme of the own authority changed
    [Arguments("https://identity.example.com:8443")] // port changed
    [Arguments("https://attacker@identity.example.com")] // user info
    [Arguments("https://identity.example.com.evil.example.net")] // own name as a host prefix
    [Arguments("https://identity.example.com/other")] // another path on the own host
    [Arguments("https://identity.example.com/?x=1")] // query
    [Arguments("{{service.identity}}")] // another template
    [Arguments("identity.example.com")] // not an absolute URL
    public async Task DeployPipelineAsync_AForeignIssuer_IsRejectedBeforeAnyStateWrite(string issuerUri)
    {
        var (adapter, _, pipeline) = ArrangeDeployablePipeline(issuerUri, tenantId: null);

        var ex = await Assert.ThrowsAsync<Exception>(async () =>
            await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId()));

        using var _ = Assert.Multiple();
        await Assert.That(ex!).IsTypeOf<AdapterServiceException>();
        await Assert.That(ex!.Message).Contains("AB#9776");
        await Assert.That(ex!.Message).Contains("pipeline-account");
        await Assert.That(ex!.Message).Contains("not this installation's identity service");
        await AdapterHubCallbacks.DidNotReceiveWithAnyArgs()
            .AdapterConfigurationUpdatedAsync(Arg.Any<string>(), Arg.Any<AdapterConfigurationDto>());
        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetPipelineDefinitionAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<string>());
    }

    [Test]
    public async Task DeployDataFlowAsync_AForeignIssuer_IsRejected()
    {
        var (_, dataFlow, _) = ArrangeDeployablePipeline("https://evil.example.net", tenantId: null);

        var ex = await Assert.ThrowsAsync<Exception>(async () =>
            await AdapterService.DeployDataFlowAsync(TenantId, dataFlow.RtId));

        using var _ = Assert.Multiple();
        await Assert.That(ex!).IsTypeOf<AdapterServiceException>();
        await Assert.That(ex!.Message).Contains("AB#9776");
        await AdapterHubCallbacks.DidNotReceiveWithAnyArgs()
            .AdapterConfigurationUpdatedAsync(Arg.Any<string>(), Arg.Any<AdapterConfigurationDto>());
    }

    [Test]
    [Arguments("energyiq")]
    [Arguments("system")]
    public async Task DeployPipelineAsync_AForeignTenant_IsRejected(string tenantId)
    {
        var (adapter, _, pipeline) = ArrangeDeployablePipeline(issuerUri: null, tenantId);

        var ex = await Assert.ThrowsAsync<Exception>(async () =>
            await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId()));

        using var _ = Assert.Multiple();
        await Assert.That(ex!).IsTypeOf<AdapterServiceException>();
        await Assert.That(ex!.Message).Contains($"names the tenant '{tenantId}'");
        await Assert.That(ex!.Message).Contains("AB#9776");
        await AdapterHubCallbacks.DidNotReceiveWithAnyArgs()
            .AdapterConfigurationUpdatedAsync(Arg.Any<string>(), Arg.Any<AdapterConfigurationDto>());
    }

    [Test]
    [Arguments(null, null)] // the AB#5115 installation default
    [Arguments("", "")]
    [Arguments("   ", "   ")]
    [Arguments("{{service.authority}}", null)] // the seed spelling (meshmakers-app integrations.yaml)
    [Arguments("{{ SERVICE.Authority }}", null)]
    [Arguments("https://identity.example.com", "tenantId")] // own authority and own tenant, explicit
    [Arguments("https://identity.example.com/", "TENANTID")] // trailing slash, tenant in another case
    [Arguments("https://IDENTITY.example.com:443", null)]
    public async Task DeployPipelineAsync_TheOwnIssuerAndTenant_StillDeploy(string? issuerUri, string? tenantId)
    {
        var (adapter, _, pipeline) = ArrangeDeployablePipeline(issuerUri, tenantId);

        await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId());

        await AdapterHubCallbacks.Received(1)
            .AdapterConfigurationUpdatedAsync(TenantId, Arg.Any<AdapterConfigurationDto>());
    }

    [Test]
    public async Task DeployPipelineAsync_TheResolvedServiceAuthority_StillDeploys()
    {
        // The value {{service.authority}} resolves to (ServiceUrls) is this installation too, even when it differs
        // from AuthorityUrl (public vs internal name).
        ControllerOptions.ServiceUrls["authority"] = "https://connect.public.example.com";
        var (adapter, _, pipeline) = ArrangeDeployablePipeline("https://connect.public.example.com/", null);

        await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId());

        await AdapterHubCallbacks.Received(1)
            .AdapterConfigurationUpdatedAsync(TenantId, Arg.Any<AdapterConfigurationDto>());
    }
}
