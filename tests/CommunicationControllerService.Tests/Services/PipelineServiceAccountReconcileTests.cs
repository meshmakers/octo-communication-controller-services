using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands.Payloads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

/// <summary>
/// AB#5111 — the declarative reconcile: <c>AssignedRoleNames</c> / <c>AllowDelegation</c> on the
/// <c>ServiceAccountConfiguration</c> are the declaration, the reconcile materialises them into the
/// identity client, and a user-initiated trigger is gated on the caller's <c>UserManagement</c>
/// role. The identity-side edge sync (add + remove) is pinned by
/// <c>ServiceAccountClientProvisioningIntegrationTests</c> in octo-identity-services; here the
/// contract under test is what goes onto the wire.
/// </summary>
internal class PipelineServiceAccountReconcileTests
{
    private const string TenantId = "tenantId";
    private const string AuthorityUrl = "https://identity.example.com";
    private const string PublicUrl = "https://communication.example.com";
    private const string ExistingSecret = "existing-secret";

    private readonly ICommunicationRepository _communicationRepository =
        Substitute.For<ICommunicationRepository>();

    private readonly ICommandClient<CreateIdentityDataCommandRequest> _commandClient =
        Substitute.For<ICommandClient<CreateIdentityDataCommandRequest>>();

    private readonly ICommunicationEventService _eventService = Substitute.For<ICommunicationEventService>();

    private readonly PipelineServiceAccountProvisioningService _sut;

    public PipelineServiceAccountReconcileTests()
    {
        ArrangeIdentityResponse(CreateIdentityDataResult.Success);

        var services = new ServiceCollection();
        services.AddSingleton(_commandClient);

        _sut = new PipelineServiceAccountProvisioningService(
            _communicationRepository,
            new PipelineServiceAccountResolver(_communicationRepository),
            services.BuildServiceProvider(),
            _eventService,
            Options.Create(new CommunicationControllerOptions
            {
                AuthorityUrl = AuthorityUrl,
                PublicUrl = PublicUrl
            }));
    }

    private void ArrangeIdentityResponse(CreateIdentityDataResult result)
    {
        _commandClient
            .GetResponse<EnumCommandResponse<CreateIdentityDataResult>>(
                Arg.Any<CreateIdentityDataCommandRequest>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new EnumCommandResponse<CreateIdentityDataResult> { Response = result });
    }

    /// <summary>
    /// The AB#5111 steady state: complete, linked, token-issuer, declarative. <paramref name="mutate"/>
    /// tweaks the entity before the repository stubs are armed.
    /// </summary>
    private RtServiceAccountConfiguration ArrangeDeclaredAdapter(RtAdapter adapter,
        Action<RtServiceAccountConfiguration>? mutate = null)
    {
        var serviceAccount = new RtServiceAccountConfiguration
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkServiceAccountConfigurationTypeId,
            RtWellKnownName = PipelineServiceAccountProvisioningService.BuildWellKnownName(adapter.RtId),
            ClientId = PipelineServiceAccountProvisioningService.BuildClientId(adapter.RtId),
            ClientSecret = ExistingSecret,
            IssuerUri = PipelineServiceAccountProvisioningService.IssuerUriToken,
            TenantId = TenantId,
            AssignedRoleNames = new AttributeStringValueList([CommonConstants.CommunicationManagementRole]),
            AllowDelegation = true
        };
        mutate?.Invoke(serviceAccount);

        _communicationRepository.GetServiceAccountForAdapterAsync(TenantId, adapter.RtId).Returns(serviceAccount);
        _communicationRepository
            .GetServiceAccountByWellKnownNameAsync(TenantId, serviceAccount.RtWellKnownName!)
            .Returns(serviceAccount);
        return serviceAccount;
    }

    /// <summary>A pre-AB#5111 entity: complete and linked, but without the declaration attributes.</summary>
    private RtServiceAccountConfiguration ArrangeLegacyAdapter(RtAdapter adapter)
    {
        var serviceAccount = new RtServiceAccountConfiguration
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkServiceAccountConfigurationTypeId,
            RtWellKnownName = PipelineServiceAccountProvisioningService.BuildWellKnownName(adapter.RtId),
            ClientId = PipelineServiceAccountProvisioningService.BuildClientId(adapter.RtId),
            ClientSecret = ExistingSecret,
            IssuerUri = AuthorityUrl,
            TenantId = TenantId
        };

        _communicationRepository.GetServiceAccountForAdapterAsync(TenantId, adapter.RtId).Returns(serviceAccount);
        _communicationRepository
            .GetServiceAccountByWellKnownNameAsync(TenantId, serviceAccount.RtWellKnownName!)
            .Returns(serviceAccount);
        return serviceAccount;
    }

    private DistClientDto SentClient()
    {
        var calls = _commandClient.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ICommandClient<CreateIdentityDataCommandRequest>.GetResponse))
            .ToList();
        return ((CreateIdentityDataCommandRequest)calls.Single().GetArguments()[0]!).Clients!.Single();
    }

    // ---------------------------------------------------------------- declaration → wire

    [Test]
    public async Task Reconcile_DeclaredRoles_AreSentVerbatim()
    {
        var adapter = RtEntityCreator.CreateAdapter();
        ArrangeDeclaredAdapter(adapter, sa => sa.AssignedRoleNames =
            new AttributeStringValueList(["AccountingRead", "AccountingWrite"]));

        var result = await _sut.ReconcileAdapterAsync(TenantId, adapter,
            ServiceAccountReconcileContext.System);

        using var _ = Assert.Multiple();
        // The identity side syncs edges to exactly this list (add + remove — pinned by the
        // identity-side integration tests); the controller's contract is sending it verbatim.
        await Assert.That(SentClient().AssignedRoleNames)
            .IsEquivalentTo(new[] { "AccountingRead", "AccountingWrite" });
        await Assert.That(result.RoleChangesSkipped).IsFalse();
    }

    [Test]
    public async Task Reconcile_HealthyDeclaredAccount_IsIdempotent()
    {
        var adapter = RtEntityCreator.CreateAdapter();
        ArrangeDeclaredAdapter(adapter);

        var first = await _sut.ReconcileAdapterAsync(TenantId, adapter,
            ServiceAccountReconcileContext.System);
        var second = await _sut.ReconcileAdapterAsync(TenantId, adapter,
            ServiceAccountReconcileContext.System);

        using var _ = Assert.Multiple();
        await Assert.That(first.Outcome).IsEqualTo(PipelineServiceAccountProvisioningOutcome.AlreadyProvisioned);
        await Assert.That(second.Outcome).IsEqualTo(PipelineServiceAccountProvisioningOutcome.AlreadyProvisioned);
        // No entity write on either pass — and the same plaintext on the wire both times, so
        // nothing ever rotates.
        await _communicationRepository.DidNotReceiveWithAnyArgs().SavePipelineServiceAccountAsync(
            Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<RtServiceAccountConfiguration>(), Arg.Any<bool>());
        var sentSecrets = _commandClient.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name ==
                        nameof(ICommandClient<CreateIdentityDataCommandRequest>.GetResponse))
            .Select(c => ((CreateIdentityDataCommandRequest)c.GetArguments()[0]!).Clients!.Single().ClientSecret!)
            .ToList();
        await Assert.That(sentSecrets).IsEquivalentTo(new[] { ExistingSecret, ExistingSecret });
    }

    [Test]
    public async Task Reconcile_DeclarationChange_NeverRotatesTheSecret()
    {
        var adapter = RtEntityCreator.CreateAdapter();
        ArrangeDeclaredAdapter(adapter, sa =>
        {
            sa.AssignedRoleNames = new AttributeStringValueList(["SomeNewRole"]);
            sa.AllowDelegation = false;
        });

        await _sut.ReconcileAdapterAsync(TenantId, adapter, ServiceAccountReconcileContext.System);

        // Only the declaration changed; the credential every running pipeline presents must
        // survive the materialisation.
        await Assert.That(SentClient().ClientSecret).IsEqualTo(ExistingSecret);
    }

    // ---------------------------------------------------------------- AllowDelegation → grants

    [Test]
    public async Task Reconcile_AllowDelegationFalse_DropsTheOnBehalfOfGrant()
    {
        var adapter = RtEntityCreator.CreateAdapter();
        ArrangeDeclaredAdapter(adapter, sa => sa.AllowDelegation = false);

        await _sut.ReconcileAdapterAsync(TenantId, adapter, ServiceAccountReconcileContext.System);

        var client = SentClient();

        using var _ = Assert.Multiple();
        // The identity consumer replaces AllowedGrantTypes wholesale, so sending the list without
        // the URN is what removes an existing grant.
        await Assert.That(client.AllowedGrantTypes).Contains("client_credentials");
        await Assert.That(client.AllowedGrantTypes.Contains(Constants.OnBehalfOfGrantType)).IsFalse();
    }

    [Test]
    public async Task Reconcile_AllowDelegationAbsent_KeepsTheOnBehalfOfGrant()
    {
        // Legacy default: every account provisioned before AB#5111 carried the grant, and an absent
        // attribute must not change behaviour.
        var adapter = RtEntityCreator.CreateAdapter();
        ArrangeLegacyAdapter(adapter);

        await _sut.ReconcileAdapterAsync(TenantId, adapter, ServiceAccountReconcileContext.System);

        await Assert.That(SentClient().AllowedGrantTypes).Contains(Constants.OnBehalfOfGrantType);
    }

    // ---------------------------------------------------------------- legacy accounts

    [Test]
    public async Task Reconcile_LegacyAccountWithoutDeclaration_LeavesIdentityRolesUntouched()
    {
        var adapter = RtEntityCreator.CreateAdapter();
        ArrangeLegacyAdapter(adapter);

        var result = await _sut.ReconcileAdapterAsync(TenantId, adapter,
            ServiceAccountReconcileContext.System);

        using var _ = Assert.Multiple();
        // 🔴 The upgrade-safety pin: a pre-AB#5111 account may carry role edges granted by hand or
        // by a blueprint (the documented delegation setup). null on the wire = the identity side
        // does not touch the edges; a declared list would sync them (removals included).
        await Assert.That(SentClient().AssignedRoleNames).IsNull();
        await Assert.That(result.Outcome).IsEqualTo(PipelineServiceAccountProvisioningOutcome.AlreadyProvisioned);
        // And the reconcile does NOT flip the entity into declarative mode behind the operator's
        // back — no write at all for a healthy legacy account.
        await _communicationRepository.DidNotReceiveWithAnyArgs().SavePipelineServiceAccountAsync(
            Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<RtServiceAccountConfiguration>(), Arg.Any<bool>());
    }

    [Test]
    public async Task Reconcile_RepairedLegacyAccount_StaysLegacy()
    {
        var adapter = RtEntityCreator.CreateAdapter();
        var legacy = ArrangeLegacyAdapter(adapter);
        legacy.IssuerUri = "https://identity.old-cluster.example.com";

        await _sut.ReconcileAdapterAsync(TenantId, adapter, ServiceAccountReconcileContext.System);

        var saved = (RtServiceAccountConfiguration)_communicationRepository.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name ==
                         nameof(ICommunicationRepository.SavePipelineServiceAccountAsync))
            .GetArguments()[2]!;

        using var _ = Assert.Multiple();
        // The issuer repair happens, but the declaration attributes stay absent.
        await Assert.That(saved.IssuerUri)
            .IsEqualTo(PipelineServiceAccountProvisioningService.IssuerUriToken);
        await Assert.That(saved.AssignedRoleNames).IsNull();
        await Assert.That(saved.ClientSecret).IsEqualTo(ExistingSecret);
    }

    [Test]
    public async Task Reconcile_IncompleteEntityWithASecret_KeepsTheSecret()
    {
        // AB#5111 tightened AB#5027 here: a repair of an unrelated attribute must not re-issue the
        // credential every running pipeline presents.
        var adapter = RtEntityCreator.CreateAdapter();
        var legacy = ArrangeLegacyAdapter(adapter);
        legacy.TenantId = "some-other-tenant";

        await _sut.ReconcileAdapterAsync(TenantId, adapter, ServiceAccountReconcileContext.System);

        var saved = (RtServiceAccountConfiguration)_communicationRepository.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name ==
                         nameof(ICommunicationRepository.SavePipelineServiceAccountAsync))
            .GetArguments()[2]!;

        using var _ = Assert.Multiple();
        await Assert.That(saved.TenantId).IsEqualTo(TenantId);
        await Assert.That(saved.ClientSecret).IsEqualTo(ExistingSecret);
        await Assert.That(SentClient().ClientSecret).IsEqualTo(ExistingSecret);
    }

    // ---------------------------------------------------------------- issuer token

    [Test]
    public async Task Reconcile_UppercaseIssuerToken_CountsAsHealthy()
    {
        var adapter = RtEntityCreator.CreateAdapter();
        ArrangeDeclaredAdapter(adapter, sa => sa.IssuerUri = "{{service.AUTHORITY}}");

        var result = await _sut.ReconcileAdapterAsync(TenantId, adapter,
            ServiceAccountReconcileContext.System);

        using var _ = Assert.Multiple();
        // Case-insensitive like the resolver itself — otherwise every sweep would "repair" a value
        // the projection resolves fine.
        await Assert.That(result.Outcome).IsEqualTo(PipelineServiceAccountProvisioningOutcome.AlreadyProvisioned);
        await _communicationRepository.DidNotReceiveWithAnyArgs().SavePipelineServiceAccountAsync(
            Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<RtServiceAccountConfiguration>(), Arg.Any<bool>());
    }

    // ---------------------------------------------------------------- security gate

    [Test]
    public async Task Reconcile_UserWithoutUserManagement_ConvergesTheClientButSkipsRoles()
    {
        var adapter = RtEntityCreator.CreateAdapter();
        ArrangeDeclaredAdapter(adapter, sa => sa.AssignedRoleNames =
            new AttributeStringValueList(["AccountingRead"]));

        var result = await _sut.ReconcileAdapterAsync(TenantId, adapter,
            ServiceAccountReconcileContext.User(callerHasUserManagementRole: false));

        using var _ = Assert.Multiple();
        // The client is still converged (secret, grants, scope) …
        await Assert.That(SentClient().ClientSecret).IsEqualTo(ExistingSecret);
        // … but no role list goes onto the wire: materialising roles is granting roles, and the
        // caller could not have granted them directly either.
        await Assert.That(SentClient().AssignedRoleNames).IsNull();
        await Assert.That(result.RoleChangesSkipped).IsTrue();
        // Loud and persistent — the account stays degraded until a privileged pass runs.
        await _eventService.Received(1).StoreWarningEventAsync(TenantId,
            Arg.Is<string>(m => m.Contains(CommonConstants.UserManagementRole)));
    }

    [Test]
    public async Task Reconcile_UserWithUserManagement_MaterializesTheDeclaredRoles()
    {
        var adapter = RtEntityCreator.CreateAdapter();
        ArrangeDeclaredAdapter(adapter, sa => sa.AssignedRoleNames =
            new AttributeStringValueList(["AccountingRead"]));

        var result = await _sut.ReconcileAdapterAsync(TenantId, adapter,
            ServiceAccountReconcileContext.User(callerHasUserManagementRole: true));

        using var _ = Assert.Multiple();
        await Assert.That(SentClient().AssignedRoleNames).IsEquivalentTo(new[] { "AccountingRead" });
        await Assert.That(result.RoleChangesSkipped).IsFalse();
        await _eventService.DidNotReceiveWithAnyArgs()
            .StoreWarningEventAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task Reconcile_UnprivilegedUserOnFreshAdapter_PersistsTheDeclarationButSendsNoRoles()
    {
        var adapter = RtEntityCreator.CreateAdapter();

        var result = await _sut.ReconcileAdapterAsync(TenantId, adapter,
            ServiceAccountReconcileContext.User(callerHasUserManagementRole: false));

        var saved = (RtServiceAccountConfiguration)_communicationRepository.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name ==
                         nameof(ICommunicationRepository.SavePipelineServiceAccountAsync))
            .GetArguments()[2]!;

        using var _ = Assert.Multiple();
        await Assert.That(result.Outcome).IsEqualTo(PipelineServiceAccountProvisioningOutcome.Provisioned);
        // The declaration is written (it is the tenant's configuration, not a grant) …
        await Assert.That(saved.AssignedRoleNames!.ToArray())
            .IsEquivalentTo(new[] { CommonConstants.CommunicationManagementRole });
        // … but nothing was materialised; the next system pass (tenant start / deploy) does that.
        await Assert.That(SentClient().AssignedRoleNames).IsNull();
        await Assert.That(result.RoleChangesSkipped).IsTrue();
    }

    // ---------------------------------------------------------------- configuration-bound entry

    [Test]
    public async Task ReconcileConfiguration_AdapterOwned_RoutesThroughTheAdapterPath()
    {
        var adapter = RtEntityCreator.CreateAdapter();
        var serviceAccount = ArrangeDeclaredAdapter(adapter);
        _communicationRepository.GetAdapterForServiceAccountAsync(TenantId, serviceAccount.RtId)
            .Returns(adapter);

        var result = await _sut.ReconcileConfigurationAsync(TenantId, serviceAccount,
            ServiceAccountReconcileContext.System);

        using var _ = Assert.Multiple();
        // The adapter defines the deterministic names — both entry points must agree on them.
        await Assert.That(result.ClientId)
            .IsEqualTo(PipelineServiceAccountProvisioningService.BuildClientId(adapter.RtId));
        await Assert.That(result.Outcome).IsEqualTo(PipelineServiceAccountProvisioningOutcome.AlreadyProvisioned);
    }

    [Test]
    public async Task ReconcileConfiguration_Standalone_UsesItsOwnClientIdAndWritesNoAdapterEdge()
    {
        var standalone = new RtServiceAccountConfiguration
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkServiceAccountConfigurationTypeId,
            RtWellKnownName = "pipeline-override-account",
            ClientId = "octo-pipeline-sa-custom",
            ClientSecret = ExistingSecret,
            IssuerUri = "https://identity.old-cluster.example.com",
            TenantId = TenantId,
            AssignedRoleNames = new AttributeStringValueList(["AccountingRead"]),
            AllowDelegation = true
        };
        _communicationRepository.GetAdapterForServiceAccountAsync(TenantId, standalone.RtId)
            .Returns((RtAdapter?)null);

        var result = await _sut.ReconcileConfigurationAsync(TenantId, standalone,
            ServiceAccountReconcileContext.System);

        var updated = (RtServiceAccountConfiguration)_communicationRepository.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(ICommunicationRepository.UpdateServiceAccountAsync))
            .GetArguments()[1]!;

        using var _ = Assert.Multiple();
        await Assert.That(result.Outcome).IsEqualTo(PipelineServiceAccountProvisioningOutcome.Repaired);
        // Its own client id, its own well-known name — nothing adapter-shaped is invented.
        await Assert.That(result.ClientId).IsEqualTo("octo-pipeline-sa-custom");
        await Assert.That(result.WellKnownName).IsEqualTo("pipeline-override-account");
        await Assert.That(SentClient().AssignedRoleNames).IsEquivalentTo(new[] { "AccountingRead" });
        // Repaired in place, keeping the secret; and never through the adapter-edge write.
        await Assert.That(updated.RtId).IsEqualTo(standalone.RtId);
        await Assert.That(updated.ClientSecret).IsEqualTo(ExistingSecret);
        await _communicationRepository.DidNotReceiveWithAnyArgs().SavePipelineServiceAccountAsync(
            Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<RtServiceAccountConfiguration>(), Arg.Any<bool>());
    }

    [Test]
    public async Task ReconcileConfiguration_HealthyStandalone_WritesNothing()
    {
        var standalone = new RtServiceAccountConfiguration
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkServiceAccountConfigurationTypeId,
            RtWellKnownName = "pipeline-override-account",
            ClientId = "octo-pipeline-sa-custom",
            ClientSecret = ExistingSecret,
            IssuerUri = PipelineServiceAccountProvisioningService.IssuerUriToken,
            TenantId = TenantId,
            AssignedRoleNames = new AttributeStringValueList(["AccountingRead"]),
            AllowDelegation = true
        };
        _communicationRepository.GetAdapterForServiceAccountAsync(TenantId, standalone.RtId)
            .Returns((RtAdapter?)null);

        var result = await _sut.ReconcileConfigurationAsync(TenantId, standalone,
            ServiceAccountReconcileContext.System);

        using var _ = Assert.Multiple();
        await Assert.That(result.Outcome).IsEqualTo(PipelineServiceAccountProvisioningOutcome.AlreadyProvisioned);
        await _communicationRepository.DidNotReceiveWithAnyArgs()
            .UpdateServiceAccountAsync(Arg.Any<string>(), Arg.Any<RtServiceAccountConfiguration>());
    }

    // ---------------------------------------------------------------- configuration-bound rotation

    [Test]
    public async Task RotateConfiguration_Standalone_RenewsBothSidesWithoutTouchingRoles()
    {
        var standalone = new RtServiceAccountConfiguration
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkServiceAccountConfigurationTypeId,
            RtWellKnownName = "pipeline-override-account",
            ClientId = "octo-pipeline-sa-custom",
            ClientSecret = ExistingSecret,
            IssuerUri = PipelineServiceAccountProvisioningService.IssuerUriToken,
            TenantId = TenantId,
            AssignedRoleNames = new AttributeStringValueList(["AccountingRead"]),
            AllowDelegation = true
        };
        _communicationRepository.GetAdapterForServiceAccountAsync(TenantId, standalone.RtId)
            .Returns((RtAdapter?)null);

        var result = await _sut.RotateConfigurationSecretAsync(TenantId, standalone);

        var updated = (RtServiceAccountConfiguration)_communicationRepository.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(ICommunicationRepository.UpdateServiceAccountAsync))
            .GetArguments()[1]!;
        var sentClient = SentClient();

        using var _ = Assert.Multiple();
        await Assert.That(result.WasCreated).IsFalse();
        await Assert.That(result.RequiresPipelineRedeploy).IsTrue();
        await Assert.That(result.ClientId).IsEqualTo("octo-pipeline-sa-custom");
        // A new secret, identical on both sides.
        await Assert.That(sentClient.ClientSecret).IsNotEqualTo(ExistingSecret);
        await Assert.That(updated.ClientSecret).IsEqualTo(sentClient.ClientSecret);
        // 🔴 A rotation must never sync roles — null on the wire leaves the edges untouched.
        await Assert.That(sentClient.AssignedRoleNames).IsNull();
        // The declaration survives the write.
        await Assert.That(updated.AssignedRoleNames!.ToArray()).IsEquivalentTo(new[] { "AccountingRead" });
    }

    [Test]
    public async Task RotateConfiguration_AdapterOwned_RoutesThroughTheAdapterPath()
    {
        var adapter = RtEntityCreator.CreateAdapter();
        var serviceAccount = ArrangeDeclaredAdapter(adapter);
        _communicationRepository.GetAdapterForServiceAccountAsync(TenantId, serviceAccount.RtId)
            .Returns(adapter);

        var result = await _sut.RotateConfigurationSecretAsync(TenantId, serviceAccount);

        using var _ = Assert.Multiple();
        await Assert.That(result.ClientId)
            .IsEqualTo(PipelineServiceAccountProvisioningService.BuildClientId(adapter.RtId));
        // The adapter path writes entity + edge in one transaction, exactly like a direct
        // adapter-scoped rotation.
        await _communicationRepository.ReceivedWithAnyArgs(1).SavePipelineServiceAccountAsync(
            Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<RtServiceAccountConfiguration>(), Arg.Any<bool>());
        await _communicationRepository.DidNotReceiveWithAnyArgs()
            .UpdateServiceAccountAsync(Arg.Any<string>(), Arg.Any<RtServiceAccountConfiguration>());
    }
}
