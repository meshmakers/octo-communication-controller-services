using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.SignalChannelServiceTests;

/// <summary>
/// AB#5143 profile display name: without one, Signal users see the assistant's number as
/// "Unknown". The name is stored on the definition, pushed to the bridge
/// (PUT /v1/profiles/{number}) when the channel reaches Registered (verify success or adoption)
/// — best-effort there: a push failure must NOT fail the registration, it becomes a detail
/// suffix on the transition's history entry. A successful push appends a ProfileUpdated entry.
/// The display-name endpoint changes the name without re-registering: attribute always stored,
/// push only while Registered (failures surface there, since the push IS the endpoint's purpose).
/// </summary>
internal class DisplayNameTests : SignalChannelServiceTestsBase
{
    private const string DisplayName = "Octo Assistant";
    private const string Code = "123456";

    [Test]
    public async Task VerifySuccess_WithDisplayName_PushesProfile_AndAppendsProfileUpdated()
    {
        var channel = ArrangeChannel(RtSignalRegistrationStateEnum.CodePending);
        channel.DisplayName = DisplayName;

        var dto = await Service.VerifyAsync(TenantId, Actor, Code);

        await BridgeClient.Received(1).UpdateProfileAsync(ApiUrl, Number, DisplayName);
        await Assert.That(dto.RegistrationState).IsEqualTo((int)RtSignalRegistrationStateEnum.Registered);
        await Assert.That(dto.DisplayName).IsEqualTo(DisplayName);

        var history = History(channel);
        await Assert.That(history.Count).IsEqualTo(2);
        await Assert.That(history[0].Action).IsEqualTo(RtSignalRegistrationActionEnum.ProfileUpdated);
        await Assert.That(history[0].Detail).Contains(DisplayName);
        await Assert.That(history[1].Action).IsEqualTo(RtSignalRegistrationActionEnum.CodeVerified);
        await Assert.That(history[1].Detail).IsEqualTo("registered");
    }

    [Test]
    public async Task VerifySuccess_WithoutDisplayName_NoPush()
    {
        var channel = ArrangeChannel(RtSignalRegistrationStateEnum.CodePending);

        await Service.VerifyAsync(TenantId, Actor, Code);

        await BridgeClient.DidNotReceiveWithAnyArgs().UpdateProfileAsync(default!, default!, default!);
        var history = History(channel);
        await Assert.That(history.Count).IsEqualTo(1);
        await Assert.That(history[0].Action).IsEqualTo(RtSignalRegistrationActionEnum.CodeVerified);
    }

    [Test]
    public async Task VerifySuccess_ProfilePushFails_RegistrationStillSucceeds_DetailNotesIt()
    {
        var channel = ArrangeChannel(RtSignalRegistrationStateEnum.CodePending);
        channel.DisplayName = DisplayName;
        BridgeClient.UpdateProfileAsync(ApiUrl, Number, DisplayName)
            .ThrowsAsync(BridgeRejected("profile boom"));

        var dto = await Service.VerifyAsync(TenantId, Actor, Code);

        // The registration stands — the push is best-effort on this path.
        await Assert.That(dto.RegistrationState).IsEqualTo((int)RtSignalRegistrationStateEnum.Registered);
        await Assert.That(channel.RegistrationState).IsEqualTo(RtSignalRegistrationStateEnum.Registered);

        var history = History(channel);
        await Assert.That(history.Count).IsEqualTo(1);
        await Assert.That(history[0].Action).IsEqualTo(RtSignalRegistrationActionEnum.CodeVerified);
        await Assert.That(history[0].Detail).Contains("registered");
        await Assert.That(history[0].Detail).Contains("profile update failed");
        await Assert.That(history[0].Detail).Contains("profile boom");
    }

    [Test]
    public async Task Adoption_WithDisplayName_PushesProfile()
    {
        BridgeClient.GetAccountsAsync(ApiUrl).Returns([Number]);
        RtSignalChannel? saved = null;
        await CommunicationRepository.SaveSignalChannelAsync(TenantId, Arg.Do<RtSignalChannel>(c => saved = c),
            Arg.Any<bool>());

        await Service.RegisterAsync(TenantId, Actor, Number, ApiUrl, null, DisplayName);

        await BridgeClient.Received(1).UpdateProfileAsync(ApiUrl, Number, DisplayName);
        var history = History(saved!);
        await Assert.That(history.Select(e => e.Action).ToList()).IsEquivalentTo([
            RtSignalRegistrationActionEnum.ProfileUpdated,
            RtSignalRegistrationActionEnum.Adopted,
            RtSignalRegistrationActionEnum.RegisterRequested
        ]);
        await Assert.That(history[0].Action).IsEqualTo(RtSignalRegistrationActionEnum.ProfileUpdated);
    }

    [Test]
    public async Task Adoption_ProfilePushFails_AdoptionStands_DetailNotesIt()
    {
        BridgeClient.GetAccountsAsync(ApiUrl).Returns([Number]);
        BridgeClient.UpdateProfileAsync(ApiUrl, Number, DisplayName)
            .ThrowsAsync(BridgeUnreachable());
        RtSignalChannel? saved = null;
        await CommunicationRepository.SaveSignalChannelAsync(TenantId, Arg.Do<RtSignalChannel>(c => saved = c),
            Arg.Any<bool>());

        var dto = await Service.RegisterAsync(TenantId, Actor, Number, ApiUrl, null, DisplayName);

        await Assert.That(dto.RegistrationState).IsEqualTo((int)RtSignalRegistrationStateEnum.Registered);
        var history = History(saved!);
        await Assert.That(history[0].Action).IsEqualTo(RtSignalRegistrationActionEnum.Adopted);
        await Assert.That(history[0].Detail).Contains("adopted");
        await Assert.That(history[0].Detail).Contains("profile update failed");
    }

    [Test]
    public async Task Register_StoresDisplayName_NoPushBeforeRegistered()
    {
        RtSignalChannel? saved = null;
        await CommunicationRepository.SaveSignalChannelAsync(TenantId, Arg.Do<RtSignalChannel>(c => saved = c),
            Arg.Any<bool>());

        var dto = await Service.RegisterAsync(TenantId, Actor, Number, ApiUrl, null, $"  {DisplayName}  ");

        // Stored (trimmed) on the definition; the push only happens on the Registered transition.
        await Assert.That(saved!.DisplayName).IsEqualTo(DisplayName);
        await Assert.That(dto.DisplayName).IsEqualTo(DisplayName);
        await Assert.That(dto.RegistrationState).IsEqualTo((int)RtSignalRegistrationStateEnum.CodePending);
        await BridgeClient.DidNotReceiveWithAnyArgs().UpdateProfileAsync(default!, default!, default!);
    }

    [Test]
    public async Task Register_WithoutDisplayName_KeepsTheStoredOne()
    {
        var existing = ArrangeChannel(RtSignalRegistrationStateEnum.Failed);
        existing.DisplayName = DisplayName;

        await Service.RegisterAsync(TenantId, Actor, Number, null, null);

        await Assert.That(existing.DisplayName).IsEqualTo(DisplayName);
    }

    [Test]
    public async Task SetDisplayName_Registered_UpdatesAttributePushesAndAudits()
    {
        var channel = ArrangeChannel(RtSignalRegistrationStateEnum.Registered);

        var dto = await Service.SetDisplayNameAsync(TenantId, Actor, DisplayName);

        await BridgeClient.Received(1).UpdateProfileAsync(ApiUrl, Number, DisplayName);
        await Assert.That(channel.DisplayName).IsEqualTo(DisplayName);
        await Assert.That(dto.DisplayName).IsEqualTo(DisplayName);

        var history = History(channel);
        await Assert.That(history.Count).IsEqualTo(1);
        await Assert.That(history[0].Action).IsEqualTo(RtSignalRegistrationActionEnum.ProfileUpdated);
        await Assert.That(history[0].Detail).Contains(DisplayName);
        await Assert.That(history[0].User).IsEqualTo(ActorAuditString);
        // Attribute save first (push-retry safety), then the audited save.
        await CommunicationRepository.Received(2).SaveSignalChannelAsync(TenantId, channel, false);
    }

    [Test]
    public async Task SetDisplayName_NotRegistered_StoresOnly_NoBridgeCall()
    {
        var channel = ArrangeChannel(RtSignalRegistrationStateEnum.CodePending);

        var dto = await Service.SetDisplayNameAsync(TenantId, Actor, DisplayName);

        await BridgeClient.DidNotReceiveWithAnyArgs().UpdateProfileAsync(default!, default!, default!);
        await Assert.That(channel.DisplayName).IsEqualTo(DisplayName);
        await Assert.That(dto.DisplayName).IsEqualTo(DisplayName);

        var history = History(channel);
        await Assert.That(history.Count).IsEqualTo(1);
        await Assert.That(history[0].Action).IsEqualTo(RtSignalRegistrationActionEnum.ProfileUpdated);
        await Assert.That(history[0].Detail).Contains("stored");
        await CommunicationRepository.Received(1).SaveSignalChannelAsync(TenantId, channel, false);
    }

    [Test]
    public async Task SetDisplayName_PushFails_KeepsStoredNameAuditsAndThrows()
    {
        var channel = ArrangeChannel(RtSignalRegistrationStateEnum.Registered);
        BridgeClient.UpdateProfileAsync(ApiUrl, Number, DisplayName)
            .ThrowsAsync(BridgeRejected("profile boom"));

        var exception = await Assert.ThrowsAsync<SignalChannelServiceException>(
            () => Service.SetDisplayNameAsync(TenantId, Actor, DisplayName));

        // Push IS this endpoint's purpose — the failure surfaces, but the stored name stays so a
        // plain retry re-pushes.
        await Assert.That(exception!.Kind).IsEqualTo(SignalChannelErrorKind.BridgeRejected);
        await Assert.That(channel.DisplayName).IsEqualTo(DisplayName);

        var history = History(channel);
        await Assert.That(history.Count).IsEqualTo(1);
        await Assert.That(history[0].Action).IsEqualTo(RtSignalRegistrationActionEnum.ProfileUpdated);
        await Assert.That(history[0].Detail).Contains("profile update failed");
        await Assert.That(history[0].Detail).Contains("profile boom");
    }

    [Test]
    public async Task SetDisplayName_NoChannel_ThrowsNotFound()
    {
        var exception = await Assert.ThrowsAsync<SignalChannelServiceException>(
            () => Service.SetDisplayNameAsync(TenantId, Actor, DisplayName));

        await Assert.That(exception!.Kind).IsEqualTo(SignalChannelErrorKind.NotFound);
        await BridgeClient.DidNotReceiveWithAnyArgs().UpdateProfileAsync(default!, default!, default!);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task SetDisplayName_Empty_ClearsStoredName_BridgeProfileUntouched(string? displayName)
    {
        // The UI sends {"displayName": ""} to clear. Only the attribute is cleared — Signal
        // profiles need a non-empty name, so the previously pushed name stays on the account.
        var channel = ArrangeChannel(RtSignalRegistrationStateEnum.Registered);
        channel.DisplayName = DisplayName;

        var dto = await Service.SetDisplayNameAsync(TenantId, Actor, displayName);

        await BridgeClient.DidNotReceiveWithAnyArgs().UpdateProfileAsync(default!, default!, default!);
        await Assert.That(channel.DisplayName).IsNull();
        await Assert.That(dto.DisplayName).IsNull();
        await CommunicationRepository.Received(1).SaveSignalChannelAsync(TenantId, channel, false);

        var history = History(channel);
        await Assert.That(history.Count).IsEqualTo(1);
        await Assert.That(history[0].Action).IsEqualTo(RtSignalRegistrationActionEnum.ProfileUpdated);
        await Assert.That(history[0].Detail).Contains("cleared");
        await Assert.That(history[0].Detail).Contains("bridge profile unchanged");
    }

    [Test]
    public async Task SetDisplayName_Clear_WorksInEveryState()
    {
        // The clear path is state-independent — it never talks to the bridge anyway.
        var channel = ArrangeChannel(RtSignalRegistrationStateEnum.CodePending);
        channel.DisplayName = DisplayName;

        await Service.SetDisplayNameAsync(TenantId, Actor, "");

        await BridgeClient.DidNotReceiveWithAnyArgs().UpdateProfileAsync(default!, default!, default!);
        await Assert.That(channel.DisplayName).IsNull();
        await Assert.That(History(channel)[0].Action)
            .IsEqualTo(RtSignalRegistrationActionEnum.ProfileUpdated);
    }

    [Test]
    public async Task GetChannel_ReturnsDisplayName()
    {
        var channel = ArrangeChannel(RtSignalRegistrationStateEnum.Registered);
        channel.DisplayName = DisplayName;
        BridgeClient.GetAccountsAsync(ApiUrl).Returns([Number]);

        var dto = await Service.GetChannelAsync(TenantId);

        await Assert.That(dto!.DisplayName).IsEqualTo(DisplayName);
    }
}
