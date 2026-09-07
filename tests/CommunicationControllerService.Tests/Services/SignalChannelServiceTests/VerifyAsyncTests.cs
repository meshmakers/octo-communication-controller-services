using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.SignalChannelServiceTests;

/// <summary>
/// AB#5143 verify flow: CodePending → Registered on success (RegisteredAt = utcnow), stays
/// CodePending with LastError on a bridge rejection, and the state gates around it.
/// </summary>
internal class VerifyAsyncTests : SignalChannelServiceTestsBase
{
    private const string Code = "123456";

    [Test]
    public async Task CodePending_Success_TransitionsToRegistered()
    {
        var channel = ArrangeChannel(RtSignalRegistrationStateEnum.CodePending);
        channel.LastError = "previous wrong code";
        var before = DateTime.UtcNow;

        var dto = await Service.VerifyAsync(TenantId, Actor, Code);

        await BridgeClient.Received(1).VerifyAsync(ApiUrl, Number, Code);
        await Assert.That(channel.RegistrationState).IsEqualTo(RtSignalRegistrationStateEnum.Registered);
        await Assert.That(channel.RegisteredAt).IsNotNull();
        await Assert.That(channel.RegisteredAt!.Value).IsGreaterThanOrEqualTo(before);
        await Assert.That(channel.LastError).IsNull();
        await CommunicationRepository.Received(1).SaveSignalChannelAsync(TenantId, channel, false);

        await Assert.That(dto.RegistrationState).IsEqualTo((int)RtSignalRegistrationStateEnum.Registered);
        await Assert.That(dto.RegisteredAt).IsEqualTo(channel.RegisteredAt);
    }

    [Test]
    public async Task DashedSmsCode_IsNormalizedToDigits()
    {
        ArrangeChannel(RtSignalRegistrationStateEnum.CodePending);

        await Service.VerifyAsync(TenantId, Actor, " 123-456 ");

        await BridgeClient.Received(1).VerifyAsync(ApiUrl, Number, "123456");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("abc123")]
    public async Task InvalidCode_ThrowsValidation(string? code)
    {
        ArrangeChannel(RtSignalRegistrationStateEnum.CodePending);

        var exception = await Assert.ThrowsAsync<SignalChannelServiceException>(
            () => Service.VerifyAsync(TenantId, Actor, code));

        await Assert.That(exception!.Kind).IsEqualTo(SignalChannelErrorKind.Validation);
        await BridgeClient.DidNotReceiveWithAnyArgs().VerifyAsync(default!, default!, default!);
    }

    [Test]
    public async Task NoChannel_ThrowsNotFound()
    {
        var exception = await Assert.ThrowsAsync<SignalChannelServiceException>(
            () => Service.VerifyAsync(TenantId, Actor, Code));

        await Assert.That(exception!.Kind).IsEqualTo(SignalChannelErrorKind.NotFound);
    }

    [Test]
    public async Task AlreadyRegistered_ThrowsConflict()
    {
        ArrangeChannel(RtSignalRegistrationStateEnum.Registered);

        var exception = await Assert.ThrowsAsync<SignalChannelServiceException>(
            () => Service.VerifyAsync(TenantId, Actor, Code));

        await Assert.That(exception!.Kind).IsEqualTo(SignalChannelErrorKind.Conflict);
        await BridgeClient.DidNotReceiveWithAnyArgs().VerifyAsync(default!, default!, default!);
    }

    [Test]
    [Arguments(RtSignalRegistrationStateEnum.Unregistered)]
    [Arguments(RtSignalRegistrationStateEnum.Failed)]
    public async Task NoVerificationPending_ThrowsConflict(RtSignalRegistrationStateEnum state)
    {
        ArrangeChannel(state);

        var exception = await Assert.ThrowsAsync<SignalChannelServiceException>(
            () => Service.VerifyAsync(TenantId, Actor, Code));

        await Assert.That(exception!.Kind).IsEqualTo(SignalChannelErrorKind.Conflict);
        await BridgeClient.DidNotReceiveWithAnyArgs().VerifyAsync(default!, default!, default!);
    }

    [Test]
    public async Task BridgeRejects_StateStaysCodePendingWithLastError()
    {
        var channel = ArrangeChannel(RtSignalRegistrationStateEnum.CodePending);
        BridgeClient.VerifyAsync(ApiUrl, Number, Code).ThrowsAsync(BridgeRejected("invalid code"));

        var exception = await Assert.ThrowsAsync<SignalChannelServiceException>(
            () => Service.VerifyAsync(TenantId, Actor, Code));

        await Assert.That(exception!.Kind).IsEqualTo(SignalChannelErrorKind.BridgeRejected);
        // The pending registration survives a wrong code — the tenant retries with the right one.
        await Assert.That(channel.RegistrationState).IsEqualTo(RtSignalRegistrationStateEnum.CodePending);
        await Assert.That(channel.LastError).Contains("invalid code");
        await Assert.That(channel.RegisteredAt).IsNull();
        await CommunicationRepository.Received(1).SaveSignalChannelAsync(TenantId, channel, false);
    }

    [Test]
    public async Task BridgeRateLimited_StateStaysCodePending_RetryAfterSurfaces()
    {
        var channel = ArrangeChannel(RtSignalRegistrationStateEnum.CodePending);
        BridgeClient.VerifyAsync(ApiUrl, Number, Code)
            .ThrowsAsync(BridgeRateLimited(TimeSpan.FromMinutes(1)));

        var exception = await Assert.ThrowsAsync<SignalChannelServiceException>(
            () => Service.VerifyAsync(TenantId, Actor, Code));

        await Assert.That(exception!.Kind).IsEqualTo(SignalChannelErrorKind.BridgeRateLimited);
        await Assert.That(exception!.RetryAfter).IsEqualTo(TimeSpan.FromMinutes(1));
        await Assert.That(channel.RegistrationState).IsEqualTo(RtSignalRegistrationStateEnum.CodePending);
    }
}
