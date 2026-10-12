using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands.Payloads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

/// <summary>
/// AB#5537 — the pipeline service account's client secret through the reveal point (reconcile,
/// rotation, presence checks) and the impersonation switch of concept decision 7 (AB#5114).
/// </summary>
internal class PipelineServiceAccountSecretTests
{
    private const string TenantId = "tenantId";
    private const string AuthorityUrl = "https://identity.example.com";
    private const string StoredSecret = "stored-plaintext-secret";

    private readonly ICommunicationRepository _repository = Substitute.For<ICommunicationRepository>();

    private readonly ICommandClient<CreateIdentityDataCommandRequest> _commandClient =
        Substitute.For<ICommandClient<CreateIdentityDataCommandRequest>>();

    private readonly ICommunicationEventService _eventService = Substitute.For<ICommunicationEventService>();
    private readonly TestSecretEncryption _secrets = TestSecretEncryption.Create();
    private readonly PipelineServiceAccountProvisioningService _sut;

    public PipelineServiceAccountSecretTests()
    {
        _commandClient
            .GetResponse<EnumCommandResponse<CreateIdentityDataResult>>(
                Arg.Any<CreateIdentityDataCommandRequest>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new EnumCommandResponse<CreateIdentityDataResult> { Response = CreateIdentityDataResult.Success });

        var services = new ServiceCollection();
        services.AddSingleton(_commandClient);
        _sut = new PipelineServiceAccountProvisioningService(_repository,
            new PipelineServiceAccountResolver(_repository), services.BuildServiceProvider(), _eventService,
            Options.Create(new CommunicationControllerOptions { AuthorityUrl = AuthorityUrl }),
            _secrets.EncryptionService);
    }

    private List<DistClientDto> SentClients()
    {
        return _commandClient.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ICommandClient<CreateIdentityDataCommandRequest>.GetResponse))
            .Select(c => ((CreateIdentityDataCommandRequest)c.GetArguments()[0]!).Clients!.Single())
            .ToList();
    }

    private RtServiceAccountConfiguration UpdatedEntity()
    {
        return (RtServiceAccountConfiguration)_repository.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(ICommunicationRepository.UpdateServiceAccountAsync))
            .GetArguments()[1]!;
    }

    private static RtServiceAccountConfiguration Account(string wellKnownName, string clientId, object? storedSecret)
    {
        var account = new RtServiceAccountConfiguration
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkServiceAccountConfigurationTypeId,
            RtWellKnownName = wellKnownName,
            ClientId = clientId,
            AssignedRoleNames = new AttributeStringValueList([CommonConstants.CommunicationManagementRole]),
            AllowDelegation = true
        };
        account.SetAttributeRawValue(nameof(RtServiceAccountConfiguration.ClientSecret), storedSecret);
        return account;
    }

    /// <summary>An adapter whose own account carries <paramref name="ownSecret" />.</summary>
    private RtAdapter ArrangeAdapterWithOwnAccount(object? ownSecret)
    {
        var adapter = RtEntityCreator.CreateAdapter();
        var own = Account(PipelineServiceAccountProvisioningService.BuildWellKnownName(adapter.RtId),
            PipelineServiceAccountProvisioningService.BuildClientId(adapter.RtId), ownSecret);
        _repository.GetServiceAccountForAdapterAsync(TenantId, adapter.RtId).Returns(own);
        _repository.GetServiceAccountByWellKnownNameAsync(TenantId, own.RtWellKnownName!).Returns(own);
        return adapter;
    }

    /// <summary>A standalone account used by one pipeline per given adapter.</summary>
    private RtServiceAccountConfiguration ArrangeStandalone(object? storedSecret, params RtAdapter[] adapters)
    {
        var standalone = Account("pipeline-override", "octo-pipeline-sa-override", storedSecret);
        _repository.GetAdapterForServiceAccountAsync(TenantId, standalone.RtId).Returns((RtAdapter?)null);
        var pipelines = new List<RtPipeline>();
        foreach (var adapter in adapters)
        {
            var pipeline = RtEntityCreator.CreatePipeline();
            _repository.GetAdapterByPipelineAsync(TenantId, pipeline.ToRtEntityId()).Returns(adapter);
            pipelines.Add(pipeline);
        }

        _repository.GetPipelinesUsingServiceAccountAsync(TenantId, standalone.RtId).Returns(pipelines);
        return standalone;
    }

    // ---------------------------------------------------------------- presence check

    [Test]
    public async Task PresenceCheck_RawStoredForms()
    {
        bool IsSet(object? raw) => _secrets.EncryptionService.DescribeSecret(raw).IsSet;
        var foreign = TestSecretEncryption.Create(keyId: "k9").Protector.Protect("x");

        using var _ = Assert.Multiple();
        await Assert.That(IsSet(null)).IsFalse();
        await Assert.That(IsSet("")).IsFalse();
        // Legacy clear text: an exact seed placeholder reads as not set until the migration converts it.
        await Assert.That(IsSet("<set me>")).IsFalse();
        await Assert.That(IsSet("TODO_SET_CLIENT_SECRET")).IsFalse();
        await Assert.That(IsSet(RtSecretValue.LegacyPlaintext("<set me>"))).IsFalse();
        await Assert.That(IsSet("x")).IsTrue();
        await Assert.That(IsSet("<a> and <b>")).IsTrue();
        await Assert.That(IsSet(_secrets.Protector.Protect("x"))).IsTrue();
        // A placeholder-looking value entered through an API is an ordinary value (decisions 2026-10-06, item 1).
        await Assert.That(IsSet(_secrets.Protector.Protect("<set me>"))).IsTrue();
        await Assert.That(IsSet(RtSecretValue.Pending("<set me>"))).IsTrue();
        await Assert.That(IsSet(RtSecretValue.LegacyPlaintext("x"))).IsTrue();
        await Assert.That(IsSet(RtSecretValue.Pending("x"))).IsTrue();
        await Assert.That(IsSet(42)).IsFalse();
        // Unknown key id: stored, but not usable (item 2).
        await Assert.That(IsSet(foreign)).IsFalse();
        await Assert.That(_secrets.EncryptionService.DescribeSecret(foreign).KeyMissing).IsTrue();
        await Assert.That(_secrets.EncryptionService.DescribeSecret(foreign).KeyId).IsEqualTo("k9");
        await Assert.That(PipelineServiceAccountProvisioningService.IsSecretUsable("<set me>")).IsTrue();
        await Assert.That(PipelineServiceAccountProvisioningService.IsSecretUsable(" ")).IsFalse();
    }

    // ---------------------------------------------------------------- reconcile / rotation

    [Test]
    public async Task Reconcile_ProtectedSecret_ResendsThePlaintextAndRotatesNothing()
    {
        var adapter = ArrangeAdapterWithOwnAccount(_secrets.Protector.Protect(StoredSecret));

        var result = await _sut.ReconcileAdapterAsync(TenantId, adapter, ServiceAccountReconcileContext.System);

        using var _ = Assert.Multiple();
        await Assert.That(result.Outcome).IsEqualTo(PipelineServiceAccountProvisioningOutcome.AlreadyProvisioned);
        await Assert.That(SentClients().Single().ClientSecret).IsEqualTo(StoredSecret);
    }

    [Test]
    public async Task Reconcile_UndecryptableSecret_AbortsBeforeAnyWrite()
    {
        // Treating it as "no secret" would rotate a live credential as a side effect.
        var adapter = ArrangeAdapterWithOwnAccount(TestSecretEncryption.Create().Protector.Protect(StoredSecret));

        var outcome = await _sut.EnsureAdapterProvisionedAsync(TenantId, adapter);

        using var _ = Assert.Multiple();
        await Assert.That(outcome).IsEqualTo(PipelineServiceAccountProvisioningOutcome.Failed);
        await Assert.That(SentClients()).IsEmpty();
        await _repository.DidNotReceiveWithAnyArgs().SavePipelineServiceAccountAsync(default!, default!, default!,
            default);
    }

    [Test]
    public async Task Reconcile_SecretWithUnknownKeyId_AbortsBeforeAnyWrite()
    {
        // Decisions 2026-10-06, item 2: KeyMissing is "unusable", not "absent" - the value becomes
        // readable once its key is added, so the reconcile must not overwrite it with a new secret.
        var adapter = ArrangeAdapterWithOwnAccount(
            TestSecretEncryption.Create(keyId: "k9").Protector.Protect(StoredSecret));

        var outcome = await _sut.EnsureAdapterProvisionedAsync(TenantId, adapter);

        using var _ = Assert.Multiple();
        await Assert.That(outcome).IsEqualTo(PipelineServiceAccountProvisioningOutcome.Failed);
        await Assert.That(SentClients()).IsEmpty();
        await _repository.DidNotReceiveWithAnyArgs().SavePipelineServiceAccountAsync(default!, default!, default!,
            default);
    }

    [Test]
    public async Task Reconcile_ProtectedPlaceholderLookingSecret_IsResentAsTheSecret()
    {
        // Decisions 2026-10-06, item 1: a "<...>" value entered through an API is a real secret.
        var adapter = ArrangeAdapterWithOwnAccount(_secrets.Protector.Protect("<set the secret>"));

        var result = await _sut.ReconcileAdapterAsync(TenantId, adapter, ServiceAccountReconcileContext.System);

        using var _ = Assert.Multiple();
        await Assert.That(result.Outcome).IsEqualTo(PipelineServiceAccountProvisioningOutcome.AlreadyProvisioned);
        await Assert.That(SentClients().Single().ClientSecret).IsEqualTo("<set the secret>");
    }

    [Test]
    public async Task ReconcileConfiguration_StandaloneWithoutSecret_AndCapableActor_StaysOnImpersonation()
    {
        var adapter = ArrangeAdapterWithOwnAccount(_secrets.Protector.Protect("adapter-own"));
        var standalone = ArrangeStandalone(null, adapter);

        var result = await _sut.ReconcileConfigurationAsync(TenantId, standalone, ServiceAccountReconcileContext.System);

        var sent = SentClients().Single();
        using var _ = Assert.Multiple();
        await Assert.That(result.Outcome).IsEqualTo(PipelineServiceAccountProvisioningOutcome.AlreadyProvisioned);
        // No secret is issued into the entity — impersonation stays in place (decision 7)...
        await _repository.DidNotReceiveWithAnyArgs().UpdateServiceAccountAsync(default!, default!);
        // ...the edges are declared, and identity gets a throwaway secret, never a stored one.
        await Assert.That(sent.MayActAsClientIds!)
            .IsEquivalentTo(new[] { PipelineServiceAccountProvisioningService.BuildClientId(adapter.RtId) });
        await Assert.That(string.IsNullOrWhiteSpace(sent.ClientSecret)).IsFalse();
    }

    [Test]
    public async Task ReconcileConfiguration_StandaloneWithoutSecret_AndOneIncapableAdapter_IssuesARealSecret()
    {
        // Strict check (same as the switch): one using adapter without a usable own secret means the
        // account cannot run on impersonation, even though another adapter could act as it.
        var capable = ArrangeAdapterWithOwnAccount(_secrets.Protector.Protect("adapter-own"));
        var incapable = ArrangeAdapterWithOwnAccount(null);
        var standalone = ArrangeStandalone(null, capable, incapable);

        var result = await _sut.ReconcileConfigurationAsync(TenantId, standalone, ServiceAccountReconcileContext.System);

        var sent = SentClients().Single();
        using var _ = Assert.Multiple();
        await Assert.That(result.Outcome).IsEqualTo(PipelineServiceAccountProvisioningOutcome.Repaired);
        await Assert.That(SecretTestValues.Plain(UpdatedEntity().ClientSecret)).IsEqualTo(sent.ClientSecret);
        await Assert.That(string.IsNullOrWhiteSpace(sent.ClientSecret)).IsFalse();
    }

    [Test]
    public async Task ReconcileConfiguration_StandaloneWithLegacyPlaceholder_AndNoActor_IssuesARealSecret()
    {
        // A legacy clear-text seed placeholder (not converted by the migration yet) reads as not set:
        // it never becomes the identity client's secret.
        var standalone = ArrangeStandalone("<set the secret>");

        var result = await _sut.ReconcileConfigurationAsync(TenantId, standalone, ServiceAccountReconcileContext.System);

        var sent = SentClients().Single();
        using var _ = Assert.Multiple();
        await Assert.That(result.Outcome).IsEqualTo(PipelineServiceAccountProvisioningOutcome.Repaired);
        await Assert.That(sent.ClientSecret).IsNotEqualTo("<set the secret>");
        await Assert.That(SecretTestValues.Plain(UpdatedEntity().ClientSecret)).IsEqualTo(sent.ClientSecret);
    }

    [Test]
    public async Task Rotate_ProtectedSecret_RestoresThePreviousPlaintextWhenTheWriteFails()
    {
        var standalone = ArrangeStandalone(_secrets.Protector.Protect(StoredSecret));
        _repository.UpdateServiceAccountAsync(TenantId, Arg.Any<RtServiceAccountConfiguration>())
            .ThrowsAsync(new InvalidOperationException("write failed"));

        await Assert.That(async () => { await _sut.RotateConfigurationSecretAsync(TenantId, standalone); })
            .Throws<InvalidOperationException>();

        var sent = SentClients();
        using var _ = Assert.Multiple();
        await Assert.That(sent.Count).IsEqualTo(2);
        await Assert.That(sent[0].ClientSecret).IsNotEqualTo(StoredSecret);
        await Assert.That(sent[1].ClientSecret).IsEqualTo(StoredSecret);
    }

    // ---------------------------------------------------------------- impersonation switch

    [Test]
    public async Task Switch_AdapterOwnAccount_IsNeverSwitched()
    {
        var adapter = ArrangeAdapterWithOwnAccount(StoredSecret);
        var own = await _repository.GetServiceAccountForAdapterAsync(TenantId, adapter.RtId);
        _repository.GetAdapterForServiceAccountAsync(TenantId, own!.RtId).Returns(adapter);

        var result = await _sut.SwitchToImpersonationAsync(TenantId, own, apply: true);

        using var _ = Assert.Multiple();
        await Assert.That(result.Outcome).IsEqualTo(ServiceAccountImpersonationSwitchOutcome.SkippedAdapterOwnIdentity);
        await Assert.That(SentClients()).IsEmpty();
        await _repository.DidNotReceiveWithAnyArgs().UpdateServiceAccountAsync(default!, default!);
    }

    [Test]
    public async Task Switch_UnusedStandalone_HasNoCapableActor()
    {
        var standalone = ArrangeStandalone(StoredSecret);

        var result = await _sut.SwitchToImpersonationAsync(TenantId, standalone, apply: true);

        await Assert.That(result.Outcome).IsEqualTo(ServiceAccountImpersonationSwitchOutcome.SkippedNoCapableActor);
    }

    [Test]
    public async Task Switch_OneUsingAdapterWithoutUsableOwnSecret_IsRefusedAndNamesIt()
    {
        var capable = ArrangeAdapterWithOwnAccount(StoredSecret);
        var incapable = ArrangeAdapterWithOwnAccount("<placeholder>");
        incapable.Name = "edge-adapter";
        var standalone = ArrangeStandalone(StoredSecret, capable, incapable);

        var result = await _sut.SwitchToImpersonationAsync(TenantId, standalone, apply: true);

        using var _ = Assert.Multiple();
        await Assert.That(result.Outcome).IsEqualTo(ServiceAccountImpersonationSwitchOutcome.SkippedNoCapableActor);
        await Assert.That(result.Message).Contains("edge-adapter");
        await _repository.DidNotReceiveWithAnyArgs().UpdateServiceAccountAsync(default!, default!);
    }

    [Test]
    public async Task Switch_SecretlessStandalone_IsAlreadyImpersonating_AndCarriesItsActorsForTheInvalidation()
    {
        var adapter = ArrangeAdapterWithOwnAccount(StoredSecret);
        var standalone = ArrangeStandalone(null, adapter);

        var result = await _sut.SwitchToImpersonationAsync(TenantId, standalone, apply: true);

        using var _ = Assert.Multiple();
        await Assert.That(result.Outcome).IsEqualTo(ServiceAccountImpersonationSwitchOutcome.AlreadyImpersonating);
        await Assert.That(result.ActorClientIds!)
            .IsEquivalentTo(new[] { PipelineServiceAccountProvisioningService.BuildClientId(adapter.RtId) });
        await Assert.That(result.CanCompleteInvalidation).IsTrue();
        await Assert.That(SentClients()).IsEmpty();
    }

    [Test]
    public async Task Switch_SecretlessStandalone_WithAnIncapableAdapter_CarriesNoActors()
    {
        var capable = ArrangeAdapterWithOwnAccount(StoredSecret);
        var incapable = ArrangeAdapterWithOwnAccount(null);
        var standalone = ArrangeStandalone(null, capable, incapable);

        var result = await _sut.SwitchToImpersonationAsync(TenantId, standalone, apply: true);

        using var _ = Assert.Multiple();
        await Assert.That(result.Outcome).IsEqualTo(ServiceAccountImpersonationSwitchOutcome.AlreadyImpersonating);
        await Assert.That(result.CanCompleteInvalidation).IsFalse();
    }

    [Test]
    public async Task Switch_ActorWithLegacyPlaintextPlaceholder_IsNotACapableActor()
    {
        // IsSecretUsable(object) cannot see inside a LegacyPlaintext value; the actor check reveals it.
        var actor = ArrangeAdapterWithOwnAccount(RtSecretValue.LegacyPlaintext("<insert secret here>"));
        actor.Name = "legacy-adapter";
        var standalone = ArrangeStandalone(_secrets.Protector.Protect(StoredSecret), actor);

        var result = await _sut.SwitchToImpersonationAsync(TenantId, standalone, apply: true);

        using var _ = Assert.Multiple();
        await Assert.That(result.Outcome).IsEqualTo(ServiceAccountImpersonationSwitchOutcome.SkippedNoCapableActor);
        await Assert.That(result.Message).Contains("legacy-adapter");
        await _repository.DidNotReceiveWithAnyArgs().UpdateServiceAccountAsync(default!, default!);
    }

    [Test]
    public async Task Switch_ActorWithLegacyPlaintextSecret_IsACapableActor()
    {
        var actor = ArrangeAdapterWithOwnAccount(RtSecretValue.LegacyPlaintext("legacy-own-secret"));
        var standalone = ArrangeStandalone(_secrets.Protector.Protect(StoredSecret), actor);

        var result = await _sut.SwitchToImpersonationAsync(TenantId, standalone, apply: false);

        await Assert.That(result.Outcome).IsEqualTo(ServiceAccountImpersonationSwitchOutcome.WouldSwitch);
    }

    [Test]
    public async Task Switch_DryRun_ClassifiesWithoutWriting()
    {
        var adapter = ArrangeAdapterWithOwnAccount(StoredSecret);
        var standalone = ArrangeStandalone(_secrets.Protector.Protect(StoredSecret), adapter);

        var result = await _sut.SwitchToImpersonationAsync(TenantId, standalone, apply: false);

        using var _ = Assert.Multiple();
        await Assert.That(result.Outcome).IsEqualTo(ServiceAccountImpersonationSwitchOutcome.WouldSwitch);
        await Assert.That(result.PipelineRtIds.Count).IsEqualTo(1);
        await Assert.That(SentClients()).IsEmpty();
        await _repository.DidNotReceiveWithAnyArgs().UpdateServiceAccountAsync(default!, default!);
    }

    [Test]
    public async Task Switch_Apply_EdgesWithCurrentSecret_ThenClear_AndLeavesTheOldSecretValid()
    {
        var adapter = ArrangeAdapterWithOwnAccount(StoredSecret);
        var standalone = ArrangeStandalone(_secrets.Protector.Protect(StoredSecret), adapter);

        var result = await _sut.SwitchToImpersonationAsync(TenantId, standalone, apply: true);

        var sent = SentClients();
        var updated = UpdatedEntity();
        using var _ = Assert.Multiple();
        await Assert.That(result.Outcome).IsEqualTo(ServiceAccountImpersonationSwitchOutcome.SwitchedPendingInvalidation);
        await Assert.That(result.SecretCleared).IsTrue();
        await Assert.That(result.CanCompleteInvalidation).IsTrue();
        // (a) edges declared with the CURRENT secret — running pipelines keep authenticating.
        await Assert.That(sent[0].ClientSecret).IsEqualTo(StoredSecret);
        await Assert.That(sent[0].MayActAsClientIds!)
            .IsEquivalentTo(new[] { PipelineServiceAccountProvisioningService.BuildClientId(adapter.RtId) });
        // (b) an explicit null clears the stored secret (String and Secret alike).
        await Assert.That(updated.Attributes.ContainsKey(nameof(RtServiceAccountConfiguration.ClientSecret))).IsTrue();
        await Assert.That(updated.Attributes[nameof(RtServiceAccountConfiguration.ClientSecret)]).IsNull();
        // (d) is NOT done here: adapters still hold the old secret until their data flows are redeployed.
        await Assert.That(sent.Count).IsEqualTo(1);
        await _eventService.ReceivedWithAnyArgs().StoreInformationEventAsync(default!, default!);
    }

    [Test]
    public async Task Switch_ClearWriteFails_ReportsFailed_AndInvalidatesNothing()
    {
        var adapter = ArrangeAdapterWithOwnAccount(StoredSecret);
        var standalone = ArrangeStandalone(_secrets.Protector.Protect(StoredSecret), adapter);
        _repository.UpdateServiceAccountAsync(TenantId, Arg.Any<RtServiceAccountConfiguration>())
            .ThrowsAsync(new InvalidOperationException("write failed"));

        var result = await _sut.SwitchToImpersonationAsync(TenantId, standalone, apply: true);

        var sent = SentClients();
        using var _ = Assert.Multiple();
        await Assert.That(result.Outcome).IsEqualTo(ServiceAccountImpersonationSwitchOutcome.Failed);
        await Assert.That(result.SecretCleared).IsFalse();
        await Assert.That(result.CanCompleteInvalidation).IsFalse();
        // Only the edge call with the CURRENT secret went out — the stored secret still works.
        await Assert.That(sent.Count).IsEqualTo(1);
        await Assert.That(sent[0].ClientSecret).IsEqualTo(StoredSecret);
    }

    [Test]
    public async Task InvalidateSwitchedSecret_SendsAThrowawaySecretAndReassertsTheEdges()
    {
        var adapter = ArrangeAdapterWithOwnAccount(StoredSecret);
        var standalone = ArrangeStandalone(null, adapter);
        _repository.GetServiceAccountByRtIdAsync(TenantId, standalone.RtId).Returns(standalone);
        var actor = PipelineServiceAccountProvisioningService.BuildClientId(adapter.RtId);

        await _sut.InvalidateSwitchedSecretAsync(TenantId, standalone, [actor]);
        await _sut.InvalidateSwitchedSecretAsync(TenantId, standalone, [actor]);

        var sent = SentClients();
        using var _ = Assert.Multiple();
        // Idempotent: each run replaces a throwaway with a throwaway.
        await Assert.That(sent.Count).IsEqualTo(2);
        await Assert.That(string.IsNullOrWhiteSpace(sent[0].ClientSecret)).IsFalse();
        await Assert.That(sent[0].ClientSecret).IsNotEqualTo(StoredSecret);
        await Assert.That(sent[0].ClientSecret).IsNotEqualTo(sent[1].ClientSecret);
        await Assert.That(sent[0].MayActAsClientIds!).IsEquivalentTo(new[] { actor });
        await _repository.DidNotReceiveWithAnyArgs().UpdateServiceAccountAsync(default!, default!);
    }

    [Test]
    public async Task InvalidateSwitchedSecret_AccountCarriesASecretAgain_IsRefused()
    {
        // A reconcile issued a fresh STORED secret meanwhile: invalidating it would break the account.
        var adapter = ArrangeAdapterWithOwnAccount(StoredSecret);
        var standalone = ArrangeStandalone(null, adapter);
        var reissued = Account(standalone.RtWellKnownName!, standalone.ClientId, _secrets.Protector.Protect("fresh"));
        _repository.GetServiceAccountByRtIdAsync(TenantId, standalone.RtId).Returns(reissued);

        await Assert.That(async () =>
            {
                await _sut.InvalidateSwitchedSecretAsync(TenantId, standalone,
                    [PipelineServiceAccountProvisioningService.BuildClientId(adapter.RtId)]);
            })
            .Throws<InvalidOperationException>();
        await Assert.That(SentClients()).IsEmpty();
    }

    [Test]
    public async Task Switch_UndecryptableSecret_FailsWithoutClearing()
    {
        var adapter = ArrangeAdapterWithOwnAccount(StoredSecret);
        var standalone = ArrangeStandalone(TestSecretEncryption.Create().Protector.Protect(StoredSecret), adapter);

        var result = await _sut.SwitchToImpersonationAsync(TenantId, standalone, apply: true);

        using var _ = Assert.Multiple();
        await Assert.That(result.Outcome).IsEqualTo(ServiceAccountImpersonationSwitchOutcome.Failed);
        await Assert.That(result.Message.Contains(StoredSecret, StringComparison.Ordinal)).IsFalse();
        await _repository.DidNotReceiveWithAnyArgs().UpdateServiceAccountAsync(default!, default!);
    }
}
