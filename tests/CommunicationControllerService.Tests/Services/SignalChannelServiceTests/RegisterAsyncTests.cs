using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.SignalChannelServiceTests;

/// <summary>
/// AB#5143 register flow: singleton creation, E.164 validation, number immutability while
/// Registered, cross-tenant number claim, apiUrl defaulting, and the bridge failure state machine.
/// </summary>
internal class RegisterAsyncTests : SignalChannelServiceTestsBase
{
    [Test]
    public async Task NoExistingChannel_CreatesSingletonAndSetsCodePending()
    {
        RtSignalChannel? saved = null;
        var isNewFlags = new List<bool>();
        await CommunicationRepository.SaveSignalChannelAsync(TenantId, Arg.Do<RtSignalChannel>(c => saved = c),
            Arg.Do<bool>(isNewFlags.Add));

        var dto = await Service.RegisterAsync(TenantId, Actor, Number, ApiUrl, null);

        await Assert.That(saved).IsNotNull();
        await Assert.That(saved!.Number).IsEqualTo(Number);
        await Assert.That(saved.ApiUrl).IsEqualTo(ApiUrl);
        await Assert.That(saved.RegistrationState).IsEqualTo(RtSignalRegistrationStateEnum.CodePending);
        await Assert.That(saved.LastError).IsNull();
        // First save (the claim) inserts, the post-bridge state write updates.
        await Assert.That(isNewFlags).IsEquivalentTo([true, false]);

        await Assert.That(dto.Number).IsEqualTo(Number);
        await Assert.That(dto.RegistrationState).IsEqualTo((int)RtSignalRegistrationStateEnum.CodePending);
        await BridgeClient.Received(1).RegisterAsync(ApiUrl, Number, null);
    }

    [Test]
    public async Task CaptchaToken_IsForwardedToTheBridge()
    {
        await Service.RegisterAsync(TenantId, Actor, Number, ApiUrl, "signalcaptcha://token");

        await BridgeClient.Received(1).RegisterAsync(ApiUrl, Number, "signalcaptcha://token");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("436641234567")] // missing '+'
    [Arguments("+43664123456a")] // non-digit
    [Arguments("+123456")] // too short (7 chars)
    [Arguments("+1234567890123456")] // too long (17 chars)
    public async Task InvalidNumber_ThrowsValidation(string? number)
    {
        var exception = await Assert.ThrowsAsync<SignalChannelServiceException>(
            () => Service.RegisterAsync(TenantId, Actor, number, ApiUrl, null));

        await Assert.That(exception!.Kind).IsEqualTo(SignalChannelErrorKind.Validation);
        await BridgeClient.DidNotReceiveWithAnyArgs().RegisterAsync(default!, default!, default);
        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SaveSignalChannelAsync(default!, default!, default);
    }

    [Test]
    [Arguments("+1234567")] // minimum: 8 chars
    [Arguments("+123456789012345")] // maximum: 16 chars
    public async Task NumberLengthBoundaries_Accepted(string number)
    {
        await Service.RegisterAsync(TenantId, Actor, number, ApiUrl, null);

        await BridgeClient.Received(1).RegisterAsync(ApiUrl, number, null);
    }

    [Test]
    public async Task ChannelRegistered_SameNumber_ThrowsConflict()
    {
        ArrangeChannel(RtSignalRegistrationStateEnum.Registered);

        var exception = await Assert.ThrowsAsync<SignalChannelServiceException>(
            () => Service.RegisterAsync(TenantId, Actor, Number, ApiUrl, null));

        await Assert.That(exception!.Kind).IsEqualTo(SignalChannelErrorKind.Conflict);
        await BridgeClient.DidNotReceiveWithAnyArgs().RegisterAsync(default!, default!, default);
    }

    [Test]
    public async Task ChannelRegistered_DifferentNumber_ThrowsConflict_NumberImmutable()
    {
        ArrangeChannel(RtSignalRegistrationStateEnum.Registered);

        var exception = await Assert.ThrowsAsync<SignalChannelServiceException>(
            () => Service.RegisterAsync(TenantId, Actor, OtherNumber, ApiUrl, null));

        await Assert.That(exception!.Kind).IsEqualTo(SignalChannelErrorKind.Conflict);
        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SaveSignalChannelAsync(default!, default!, default);
    }

    [Test]
    [Arguments(RtSignalRegistrationStateEnum.Unregistered)]
    [Arguments(RtSignalRegistrationStateEnum.CodePending)]
    [Arguments(RtSignalRegistrationStateEnum.Failed)]
    public async Task ExistingChannelNotRegistered_ReRegisterAllowed_ReusesSingleton(
        RtSignalRegistrationStateEnum state)
    {
        var existing = ArrangeChannel(state);

        var isNewFlags = new List<bool>();
        await CommunicationRepository.SaveSignalChannelAsync(TenantId, Arg.Any<RtSignalChannel>(),
            Arg.Do<bool>(isNewFlags.Add));

        await Service.RegisterAsync(TenantId, Actor, OtherNumber, null, null);

        // The singleton is re-used (updates only, no insert) and the number may still change
        // before the Registered state locks it.
        await Assert.That(isNewFlags).IsEquivalentTo([false, false]);
        await Assert.That(existing.Number).IsEqualTo(OtherNumber);
        await Assert.That(existing.RegistrationState).IsEqualTo(RtSignalRegistrationStateEnum.CodePending);
    }

    [Test]
    public async Task NumberClaimedByOtherTenant_ThrowsConflict()
    {
        AdapterCache.GetEnabledTenantIds().Returns([TenantId, OtherTenantId]);
        ArrangeChannel(RtSignalRegistrationStateEnum.CodePending, OtherTenantId);

        var exception = await Assert.ThrowsAsync<SignalChannelServiceException>(
            () => Service.RegisterAsync(TenantId, Actor, Number, ApiUrl, null));

        await Assert.That(exception!.Kind).IsEqualTo(SignalChannelErrorKind.Conflict);
        await BridgeClient.DidNotReceiveWithAnyArgs().RegisterAsync(default!, default!, default);
        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SaveSignalChannelAsync(default!, default!, default);
    }

    [Test]
    public async Task OtherTenantHoldsDifferentNumber_NoConflict()
    {
        AdapterCache.GetEnabledTenantIds().Returns([TenantId, OtherTenantId]);
        ArrangeChannel(RtSignalRegistrationStateEnum.Registered, OtherTenantId, OtherNumber);

        var dto = await Service.RegisterAsync(TenantId, Actor, Number, ApiUrl, null);

        await Assert.That(dto.RegistrationState).IsEqualTo((int)RtSignalRegistrationStateEnum.CodePending);
    }

    [Test]
    public async Task UnreadableForeignTenant_IsSkipped_RegistrationProceeds()
    {
        AdapterCache.GetEnabledTenantIds().Returns([TenantId, OtherTenantId]);
        CommunicationRepository.GetSignalChannelsAsync(OtherTenantId)
            .ThrowsAsync(new InvalidOperationException("tenant unreadable"));

        var dto = await Service.RegisterAsync(TenantId, Actor, Number, ApiUrl, null);

        await Assert.That(dto.RegistrationState).IsEqualTo((int)RtSignalRegistrationStateEnum.CodePending);
    }

    [Test]
    public async Task NoApiUrlInRequest_FallsBackToExistingDefinition()
    {
        ArrangeChannel(RtSignalRegistrationStateEnum.Failed);

        await Service.RegisterAsync(TenantId, Actor, Number, null, null);

        await BridgeClient.Received(1).RegisterAsync(ApiUrl, Number, null);
    }

    [Test]
    public async Task NoApiUrlAnywhere_FallsBackToInstanceDefault()
    {
        var dto = await Service.RegisterAsync(TenantId, Actor, Number, null, null);

        await BridgeClient.Received(1).RegisterAsync(DefaultApiUrl, Number, null);
        await Assert.That(dto.ApiUrl).IsEqualTo(DefaultApiUrl);
    }

    [Test]
    public async Task BridgeRejects_StateFailedWithLastError_AndThrows()
    {
        RtSignalChannel? saved = null;
        await CommunicationRepository.SaveSignalChannelAsync(TenantId, Arg.Do<RtSignalChannel>(c => saved = c),
            Arg.Any<bool>());
        BridgeClient.RegisterAsync(ApiUrl, Number, null).ThrowsAsync(BridgeRejected("number blocked"));

        var exception = await Assert.ThrowsAsync<SignalChannelServiceException>(
            () => Service.RegisterAsync(TenantId, Actor, Number, ApiUrl, null));

        await Assert.That(exception!.Kind).IsEqualTo(SignalChannelErrorKind.BridgeRejected);
        await Assert.That(saved!.RegistrationState).IsEqualTo(RtSignalRegistrationStateEnum.Failed);
        await Assert.That(saved.LastError).Contains("number blocked");
    }

    [Test]
    public async Task BridgeRateLimited_StateStaysUnregistered_RetryAfterSurfaces()
    {
        RtSignalChannel? saved = null;
        await CommunicationRepository.SaveSignalChannelAsync(TenantId, Arg.Do<RtSignalChannel>(c => saved = c),
            Arg.Any<bool>());
        BridgeClient.RegisterAsync(ApiUrl, Number, null)
            .ThrowsAsync(BridgeRateLimited(TimeSpan.FromSeconds(30)));

        var exception = await Assert.ThrowsAsync<SignalChannelServiceException>(
            () => Service.RegisterAsync(TenantId, Actor, Number, ApiUrl, null));

        await Assert.That(exception!.Kind).IsEqualTo(SignalChannelErrorKind.BridgeRateLimited);
        await Assert.That(exception!.RetryAfter).IsEqualTo(TimeSpan.FromSeconds(30));
        // Nothing was decided on the Signal side — a plain retry stays possible.
        await Assert.That(saved!.RegistrationState).IsEqualTo(RtSignalRegistrationStateEnum.Unregistered);
        await Assert.That(saved.LastError).IsNotNull();
    }

    [Test]
    public async Task BridgeUnreachable_StateStaysUnregistered_AndThrows()
    {
        RtSignalChannel? saved = null;
        await CommunicationRepository.SaveSignalChannelAsync(TenantId, Arg.Do<RtSignalChannel>(c => saved = c),
            Arg.Any<bool>());
        BridgeClient.RegisterAsync(ApiUrl, Number, null).ThrowsAsync(BridgeUnreachable());

        var exception = await Assert.ThrowsAsync<SignalChannelServiceException>(
            () => Service.RegisterAsync(TenantId, Actor, Number, ApiUrl, null));

        await Assert.That(exception!.Kind).IsEqualTo(SignalChannelErrorKind.BridgeUnreachable);
        await Assert.That(saved!.RegistrationState).IsEqualTo(RtSignalRegistrationStateEnum.Unregistered);
    }

    [Test]
    public async Task ReRegisterAfterFailure_ClearsErrorState()
    {
        var existing = ArrangeChannel(RtSignalRegistrationStateEnum.Failed);
        existing.LastError = "old error";
        existing.RegisteredAt = null;

        await Service.RegisterAsync(TenantId, Actor, Number, null, null);

        await Assert.That(existing.RegistrationState).IsEqualTo(RtSignalRegistrationStateEnum.CodePending);
        await Assert.That(existing.LastError).IsNull();
    }
}
