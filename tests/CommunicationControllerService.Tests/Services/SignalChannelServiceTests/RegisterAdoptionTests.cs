using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.SignalChannelServiceTests;

/// <summary>
/// AB#5143 adopt-existing-bridge-account flow: when the bridge already holds an account for the
/// requested number (pre-existing dev account, or a backup/restore migration of the bridge
/// state), a POST /v1/register would answer 400 "Account is already registered" — so the register
/// flow adopts the account instead: state goes straight to Registered, no verification, an
/// "Adopted" audit entry. The tenant guards (singleton, cross-tenant claim, immutability while
/// Registered) still apply BEFORE adoption; an unreadable account list falls back to the plain
/// register attempt.
/// </summary>
internal class RegisterAdoptionTests : SignalChannelServiceTestsBase
{
    [Test]
    public async Task NumberAlreadyOnBridge_AdoptsAccount_NoRegisterNoVerify()
    {
        BridgeClient.GetAccountsAsync(ApiUrl).Returns([OtherNumber, Number]);
        RtSignalChannel? saved = null;
        await CommunicationRepository.SaveSignalChannelAsync(TenantId, Arg.Do<RtSignalChannel>(c => saved = c),
            Arg.Any<bool>());
        var before = DateTime.UtcNow;

        var dto = await Service.RegisterAsync(TenantId, Actor, Number, ApiUrl, null);

        // The caller detects registrationState = Registered (2) and skips the verify step.
        await Assert.That(dto.RegistrationState).IsEqualTo((int)RtSignalRegistrationStateEnum.Registered);
        await Assert.That(dto.RegisteredAt).IsNotNull();
        await Assert.That(saved!.RegistrationState).IsEqualTo(RtSignalRegistrationStateEnum.Registered);
        await Assert.That(saved.RegisteredAt!.Value).IsGreaterThanOrEqualTo(before);
        await Assert.That(saved.LastError).IsNull();
        await BridgeClient.DidNotReceiveWithAnyArgs().RegisterAsync(default!, default!, default);
        await BridgeClient.DidNotReceiveWithAnyArgs().VerifyAsync(default!, default!, default!);
    }

    [Test]
    public async Task Adoption_AppendsAdoptedAuditEntry_NewestFirst()
    {
        BridgeClient.GetAccountsAsync(ApiUrl).Returns([Number]);
        RtSignalChannel? saved = null;
        await CommunicationRepository.SaveSignalChannelAsync(TenantId, Arg.Do<RtSignalChannel>(c => saved = c),
            Arg.Any<bool>());

        await Service.RegisterAsync(TenantId, Actor, Number, ApiUrl, null);

        var history = History(saved!);
        await Assert.That(history.Select(e => e.Action))
            .IsEquivalentTo([
                RtSignalRegistrationActionEnum.Adopted,
                RtSignalRegistrationActionEnum.RegisterRequested
            ]);
        await Assert.That(history[0].Detail).Contains("adopted");
        await Assert.That(history[0].User).IsEqualTo(ActorAuditString);
    }

    [Test]
    public async Task Adoption_ReusesExistingSingleton()
    {
        var existing = ArrangeChannel(RtSignalRegistrationStateEnum.Failed);
        BridgeClient.GetAccountsAsync(ApiUrl).Returns([Number]);
        var isNewFlags = new List<bool>();
        await CommunicationRepository.SaveSignalChannelAsync(TenantId, Arg.Any<RtSignalChannel>(),
            Arg.Do<bool>(isNewFlags.Add));

        await Service.RegisterAsync(TenantId, Actor, Number, null, null);

        // Claim save + adoption save, both updates on the singleton.
        await Assert.That(isNewFlags).IsEquivalentTo([false, false]);
        await Assert.That(existing.RegistrationState).IsEqualTo(RtSignalRegistrationStateEnum.Registered);
    }

    [Test]
    public async Task AccountListUnreachable_FallsBackToPlainRegister()
    {
        BridgeClient.GetAccountsAsync(ApiUrl).ThrowsAsync(BridgeUnreachable());

        var dto = await Service.RegisterAsync(TenantId, Actor, Number, ApiUrl, null);

        // Bridge errors of the plain register then surface exactly as without the adoption check.
        await BridgeClient.Received(1).RegisterAsync(ApiUrl, Number, null);
        await Assert.That(dto.RegistrationState).IsEqualTo((int)RtSignalRegistrationStateEnum.CodePending);
    }

    [Test]
    public async Task AlreadyRegisteredChannel_ConflictBeforeAdoption()
    {
        ArrangeChannel(RtSignalRegistrationStateEnum.Registered);
        BridgeClient.GetAccountsAsync(ApiUrl).Returns([Number]);

        var exception = await Assert.ThrowsAsync<SignalChannelServiceException>(
            () => Service.RegisterAsync(TenantId, Actor, Number, ApiUrl, null));

        await Assert.That(exception!.Kind).IsEqualTo(SignalChannelErrorKind.Conflict);
        await BridgeClient.DidNotReceiveWithAnyArgs().GetAccountsAsync(default!);
    }

    [Test]
    public async Task NumberClaimedByOtherTenant_ConflictBeforeAdoption()
    {
        // The cross-tenant claim scan runs before the bridge is even asked — a number claimed by
        // another tenant of THIS instance can never be adopted. (A claim held by another OctoMesh
        // instance sharing the cluster bridge is not detectable; the NetworkPolicy trust boundary
        // covers that.)
        AdapterCache.GetEnabledTenantIds().Returns([TenantId, OtherTenantId]);
        ArrangeChannel(RtSignalRegistrationStateEnum.Registered, OtherTenantId);
        BridgeClient.GetAccountsAsync(ApiUrl).Returns([Number]);

        var exception = await Assert.ThrowsAsync<SignalChannelServiceException>(
            () => Service.RegisterAsync(TenantId, Actor, Number, ApiUrl, null));

        await Assert.That(exception!.Kind).IsEqualTo(SignalChannelErrorKind.Conflict);
        await BridgeClient.DidNotReceiveWithAnyArgs().GetAccountsAsync(default!);
        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SaveSignalChannelAsync(default!, default!, default);
    }

    [Test]
    public async Task Adoption_StoresInformationEvent()
    {
        BridgeClient.GetAccountsAsync(ApiUrl).Returns([Number]);

        await Service.RegisterAsync(TenantId, Actor, Number, ApiUrl, null);

        await CommunicationEventService.Received(1)
            .StoreInformationEventAsync(TenantId, Arg.Is<string>(m => m.Contains("adopted")));
    }
}
