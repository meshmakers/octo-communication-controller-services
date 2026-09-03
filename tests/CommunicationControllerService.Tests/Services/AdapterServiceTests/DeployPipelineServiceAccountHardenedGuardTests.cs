using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

/// <summary>
/// AB#5112 (Epic AB#4979) hardened deploy guard: beyond AB#5027's "resolvable at all", the
/// resolved account must hold a client secret (refused unconditionally — a local fact), and its
/// identity client must exist (refused only on an authoritative identity answer, gated by
/// <c>ServiceAccountGuard:CheckIdentityClient</c>; an unreachable identity service is
/// deliberately NON-blocking so identity downtime cannot brick pipeline deploys).
/// </summary>
internal class DeployPipelineServiceAccountHardenedGuardTests : AdapterServiceTestsBase
{
    private const string PipelineDefinition =
        """
        triggers:
          - type: FromHttpRequest@1
        """;

    private (RtAdapter Adapter, RtDataFlow DataFlow, RtPipeline Pipeline) ArrangeDeployablePipeline()
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
        CommunicationRepository.GetConfigurationsByPipelineAsync(TenantId, rtPipeline.RtId)
            .Returns(Task.FromResult<IEnumerable<RtConfiguration>>([]));

        return (rtAdapter, rtDataFlow, rtPipeline);
    }

    /// <summary>
    /// Replaces the base's complete default account with one that resolves fine (AB#5027 passes)
    /// but holds no client secret — the state AB#5112 exists to catch.
    /// </summary>
    private void ArrangeSecretlessServiceAccount()
    {
        var secretless = new RtServiceAccountConfiguration
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkServiceAccountConfigurationTypeId,
            RtWellKnownName = "secretless-account",
            ClientId = "octo-pipeline-sa-secretless",
            IssuerUri = "https://identity.example.com",
            TenantId = TenantId
            // ClientSecret deliberately never written — GetAttributeValueOrDefault yields null.
        };
        CommunicationRepository
            .GetServiceAccountForAdapterAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>())
            .Returns(secretless);
    }

    // ---------------------------------------------------------------- secret missing

    [Test]
    public async Task DeployPipelineAsync_ServiceAccountWithoutSecret_IsRejectedWithReconcileRemedy()
    {
        var (adapter, _, pipeline) = ArrangeDeployablePipeline();
        ArrangeSecretlessServiceAccount();

        var ex = await Assert.ThrowsAsync<Exception>(async () =>
            await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId()));

        using var _ = Assert.Multiple();
        await Assert.That(ex!).IsTypeOf<AdapterServiceException>();
        // Cause, work item, affected objects and the reconcile remedy must be in the message.
        await Assert.That(ex!.Message).Contains("client secret");
        await Assert.That(ex!.Message).Contains("AB#5112");
        await Assert.That(ex!.Message).Contains("secretless-account");
        await Assert.That(ex!.Message).Contains("mesh-adapter");
        await Assert.That(ex!.Message).Contains("reconcile");
        await Assert.That(ex!.Message).Contains("Studio");
        // The refusal happens before any state write.
        await AdapterHubCallbacks.DidNotReceiveWithAnyArgs()
            .AdapterConfigurationUpdatedAsync(Arg.Any<string>(), Arg.Any<AdapterConfigurationDto>());
        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetPipelineDefinitionAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<string>());
    }

    [Test]
    public async Task DeployDataFlowAsync_ServiceAccountWithoutSecret_IsRejected()
    {
        var (_, dataFlow, _) = ArrangeDeployablePipeline();
        ArrangeSecretlessServiceAccount();

        var ex = await Assert.ThrowsAsync<Exception>(async () =>
            await AdapterService.DeployDataFlowAsync(TenantId, dataFlow.RtId));

        using var _ = Assert.Multiple();
        await Assert.That(ex!).IsTypeOf<AdapterServiceException>();
        await Assert.That(ex!.Message).Contains("AB#5112");
        await AdapterHubCallbacks.DidNotReceiveWithAnyArgs()
            .AdapterConfigurationUpdatedAsync(Arg.Any<string>(), Arg.Any<AdapterConfigurationDto>());
    }

    [Test]
    public async Task DeployPipelineAsync_SecretMissing_RefusesEvenWithClientCheckDisabled()
    {
        var (adapter, _, pipeline) = ArrangeDeployablePipeline();
        ArrangeSecretlessServiceAccount();
        GuardOptions.CheckIdentityClient = false;

        // The secret check is not behind the option: it is a local fact, free to evaluate, and a
        // secretless account can never authenticate regardless of identity state.
        var ex = await Assert.ThrowsAsync<Exception>(async () =>
            await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId()));

        await Assert.That(ex!.Message).Contains("client secret");
    }

    // ---------------------------------------------------------------- identity client missing

    [Test]
    public async Task DeployPipelineAsync_IdentityClientMissing_IsRejectedWithReconcileRemedy()
    {
        var (adapter, _, pipeline) = ArrangeDeployablePipeline();
        IdentityClientReader
            .GetClientAsync(TenantId, Arg.Any<string>(), Arg.Any<bool>())
            .Returns(IdentityClientLookup.NotFound);

        var ex = await Assert.ThrowsAsync<Exception>(async () =>
            await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId()));

        using var _ = Assert.Multiple();
        await Assert.That(ex!).IsTypeOf<AdapterServiceException>();
        await Assert.That(ex!.Message).Contains("does not exist");
        await Assert.That(ex!.Message).Contains("AB#5112");
        await Assert.That(ex!.Message).Contains("client-id"); // the account's ClientId (RtEntityCreator)
        await Assert.That(ex!.Message).Contains("reconcile");
        await AdapterHubCallbacks.DidNotReceiveWithAnyArgs()
            .AdapterConfigurationUpdatedAsync(Arg.Any<string>(), Arg.Any<AdapterConfigurationDto>());
    }

    [Test]
    public async Task DeployDataFlowAsync_IdentityClientMissing_IsRejected()
    {
        var (_, dataFlow, _) = ArrangeDeployablePipeline();
        IdentityClientReader
            .GetClientAsync(TenantId, Arg.Any<string>(), Arg.Any<bool>())
            .Returns(IdentityClientLookup.NotFound);

        var ex = await Assert.ThrowsAsync<Exception>(async () =>
            await AdapterService.DeployDataFlowAsync(TenantId, dataFlow.RtId));

        await Assert.That(ex!.Message).Contains("AB#5112");
    }

    [Test]
    public async Task DeployPipelineAsync_IdentityClientMissing_OptionDisabled_Deploys()
    {
        var (adapter, _, pipeline) = ArrangeDeployablePipeline();
        IdentityClientReader
            .GetClientAsync(TenantId, Arg.Any<string>(), Arg.Any<bool>())
            .Returns(IdentityClientLookup.NotFound);
        GuardOptions.CheckIdentityClient = false;

        await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId());

        using var _ = Assert.Multiple();
        await AdapterHubCallbacks.Received(1)
            .AdapterConfigurationUpdatedAsync(TenantId, Arg.Any<AdapterConfigurationDto>());
        // The rollout switch turns the whole identity round trip off, not just the refusal.
        await IdentityClientReader.DidNotReceiveWithAnyArgs()
            .GetClientAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    // ---------------------------------------------------------------- identity unreachable

    [Test]
    public async Task DeployPipelineAsync_IdentityUnreachable_DeploysAnyway()
    {
        var (adapter, _, pipeline) = ArrangeDeployablePipeline();
        IdentityClientReader
            .GetClientAsync(TenantId, Arg.Any<string>(), Arg.Any<bool>())
            .Returns(IdentityClientLookup.Unavailable("the identity service could not be queried: connection refused"));

        // Identity downtime must never brick a pipeline deploy: the lookup failure is a warning,
        // not a refusal (the adapter-side token request surfaces a real problem immediately anyway).
        await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId());

        await AdapterHubCallbacks.Received(1)
            .AdapterConfigurationUpdatedAsync(TenantId, Arg.Any<AdapterConfigurationDto>());
    }

    [Test]
    public async Task DeployDataFlowAsync_IdentityUnreachable_DeploysAnyway()
    {
        var (_, dataFlow, _) = ArrangeDeployablePipeline();
        IdentityClientReader
            .GetClientAsync(TenantId, Arg.Any<string>(), Arg.Any<bool>())
            .Returns(IdentityClientLookup.Unavailable("no caller bearer token is available"));

        await AdapterService.DeployDataFlowAsync(TenantId, dataFlow.RtId);

        await AdapterHubCallbacks.Received(1)
            .AdapterConfigurationUpdatedAsync(TenantId, Arg.Any<AdapterConfigurationDto>());
    }

    // ---------------------------------------------------------------- happy path

    [Test]
    public async Task DeployPipelineAsync_IdentityClientExists_DeploysAndVerifiedTheRightClient()
    {
        var (adapter, _, pipeline) = ArrangeDeployablePipeline();

        await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId());

        using var _ = Assert.Multiple();
        await AdapterHubCallbacks.Received(1)
            .AdapterConfigurationUpdatedAsync(TenantId, Arg.Any<AdapterConfigurationDto>());
        // The guard asks for exactly the resolved account's client, without the role detail —
        // existence is the deploy question, drift belongs to the health endpoint.
        await IdentityClientReader.Received(1).GetClientAsync(TenantId, "client-id", false);
    }
}
