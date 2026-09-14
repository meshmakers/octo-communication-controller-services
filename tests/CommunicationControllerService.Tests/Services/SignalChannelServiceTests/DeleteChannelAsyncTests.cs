using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.SignalChannelServiceTests;

/// <summary>
/// AB#5143 delete flow: works from EVERY state, the bridge account is only unregistered when this
/// definition owns the registration (state Registered — a stuck CodePending/Failed definition may
/// reference a working bridge account it does not own, which must never be destroyed), the
/// unregister is best-effort (a "not registered" answer or an unreachable bridge never blocks the
/// delete), and the delete is what frees the number for the next claimant.
/// </summary>
internal class DeleteChannelAsyncTests : SignalChannelServiceTestsBase
{
    [Test]
    public async Task Registered_UnregistersBridgeAndDeletesDefinition()
    {
        var channel = ArrangeChannel(RtSignalRegistrationStateEnum.Registered);

        await Service.DeleteChannelAsync(TenantId, Actor);

        await BridgeClient.Received(1).UnregisterAsync(ApiUrl, Number);
        await CommunicationRepository.Received(1).DeleteSignalChannelAsync(TenantId,
            Arg.Is<RtEntityId>(id => id.RtId == channel.RtId));
    }

    [Test]
    [Arguments(RtSignalRegistrationStateEnum.Unregistered)]
    [Arguments(RtSignalRegistrationStateEnum.CodePending)]
    [Arguments(RtSignalRegistrationStateEnum.Failed)]
    public async Task NotRegistered_DeletesDefinitionOnly_BridgeAccountUntouched(
        RtSignalRegistrationStateEnum state)
    {
        // The definition never owned the registration — the bridge may hold a healthy account for
        // this number that belongs to someone else (the adopt scenario). Unregistering with
        // delete_local_data would destroy that working account.
        var channel = ArrangeChannel(state);

        await Service.DeleteChannelAsync(TenantId, Actor);

        await BridgeClient.DidNotReceiveWithAnyArgs().UnregisterAsync(default!, default!);
        await CommunicationRepository.Received(1).DeleteSignalChannelAsync(TenantId,
            Arg.Is<RtEntityId>(id => id.RtId == channel.RtId));
    }

    [Test]
    public async Task BridgeAnswersNotRegistered_DeleteStillHappens()
    {
        var channel = ArrangeChannel(RtSignalRegistrationStateEnum.Registered);
        BridgeClient.UnregisterAsync(ApiUrl, Number)
            .ThrowsAsync(BridgeRejected("account is not registered"));

        await Service.DeleteChannelAsync(TenantId, Actor);

        await CommunicationRepository.Received(1).DeleteSignalChannelAsync(TenantId,
            Arg.Is<RtEntityId>(id => id.RtId == channel.RtId));
    }

    [Test]
    public async Task BridgeUnreachable_DeleteStillHappens()
    {
        var channel = ArrangeChannel(RtSignalRegistrationStateEnum.Registered);
        BridgeClient.UnregisterAsync(ApiUrl, Number).ThrowsAsync(BridgeUnreachable());

        await Service.DeleteChannelAsync(TenantId, Actor);

        await CommunicationRepository.Received(1).DeleteSignalChannelAsync(TenantId,
            Arg.Is<RtEntityId>(id => id.RtId == channel.RtId));
    }

    [Test]
    public async Task NoChannel_ThrowsNotFound()
    {
        var exception = await Assert.ThrowsAsync<SignalChannelServiceException>(
            () => Service.DeleteChannelAsync(TenantId, Actor));

        await Assert.That(exception!.Kind).IsEqualTo(SignalChannelErrorKind.NotFound);
        await BridgeClient.DidNotReceiveWithAnyArgs().UnregisterAsync(default!, default!);
        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .DeleteSignalChannelAsync(default!, default!);
    }

    [Test]
    public async Task NumberBecomesClaimableAgain_AfterDelete()
    {
        // The OTHER tenant once claimed the number but deleted its channel (no definition is
        // stored any more) — this tenant's register succeeds.
        AdapterCache.GetEnabledTenantIds().Returns([TenantId, OtherTenantId]);
        CommunicationRepository.GetSignalChannelsAsync(OtherTenantId)
            .Returns(Array.Empty<RtSignalChannel>());

        var dto = await Service.RegisterAsync(TenantId, Actor, Number, ApiUrl, null);

        await Assert.That(dto.RegistrationState).IsEqualTo((int)RtSignalRegistrationStateEnum.CodePending);
    }
}
