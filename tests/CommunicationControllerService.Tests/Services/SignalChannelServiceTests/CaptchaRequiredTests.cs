using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.SignalChannelServiceTests;

/// <summary>
/// AB#5143 captcha-required lane: the Studio wizard attempts register WITHOUT a captcha first and
/// only shows the captcha step when Signal actually demands one — so the captcha rejection must be
/// machine-readable. The bridge client detects signal-cli's "Captcha required" message (typed flag
/// on <see cref="SignalBridgeException"/>, the single string-match), the service maps it onto
/// <see cref="SignalChannelErrorKind.BridgeCaptchaRequired"/>, and the controller answers 422
/// instead of 400. Everything else (Failed state, LastError, the RegisterFailed history entry) is
/// identical to a plain bridge rejection.
/// </summary>
internal class CaptchaRequiredTests : SignalChannelServiceTestsBase
{
    [Test]
    [Arguments("Captcha required for verification, use --captcha CAPTCHA")]
    [Arguments("captcha required")]
    [Arguments("CAPTCHA REQUIRED (proof of work)")]
    public async Task CaptchaMessages_SetTheTypedFlag(string error)
    {
        var exception = SignalBridgeException.Rejected(400, error);

        await Assert.That(exception.IsCaptchaRequired).IsTrue();
        await Assert.That(exception.Kind).IsEqualTo(SignalBridgeErrorKind.Rejected);
    }

    [Test]
    [Arguments("number blocked")]
    [Arguments("Account is already registered")]
    [Arguments("captcha invalid")] // wrong captcha is a plain rejection, not a captcha demand
    public async Task OtherRejections_DoNotSetTheFlag(string error)
    {
        var exception = SignalBridgeException.Rejected(400, error);

        await Assert.That(exception.IsCaptchaRequired).IsFalse();
    }

    [Test]
    public async Task RegisterWithoutCaptcha_BridgeDemandsOne_KindIsBridgeCaptchaRequired()
    {
        RtSignalChannel? saved = null;
        await CommunicationRepository.SaveSignalChannelAsync(TenantId, Arg.Do<RtSignalChannel>(c => saved = c),
            Arg.Any<bool>());
        BridgeClient.RegisterAsync(ApiUrl, Number, null).ThrowsAsync(BridgeCaptchaRequired());

        var exception = await Assert.ThrowsAsync<SignalChannelServiceException>(
            () => Service.RegisterAsync(TenantId, Actor, Number, ApiUrl, null));

        // The distinct kind is what the controller turns into 422; state machine and error
        // surface behave exactly like a plain rejection.
        await Assert.That(exception!.Kind).IsEqualTo(SignalChannelErrorKind.BridgeCaptchaRequired);
        await Assert.That(exception.Message).Contains("Captcha required");
        await Assert.That(saved!.RegistrationState).IsEqualTo(RtSignalRegistrationStateEnum.Failed);
        await Assert.That(saved.LastError).Contains("Captcha required");
    }

    [Test]
    public async Task CaptchaDemand_StillWritesRegisterFailedHistoryExactlyOnce()
    {
        var snapshots = new List<List<RtSignalRegistrationEventRecord>>();
        await CommunicationRepository.SaveSignalChannelAsync(TenantId,
            Arg.Do<RtSignalChannel>(c => snapshots.Add(History(c))), Arg.Any<bool>());
        BridgeClient.RegisterAsync(ApiUrl, Number, null).ThrowsAsync(BridgeCaptchaRequired());

        await Assert.ThrowsAsync<SignalChannelServiceException>(
            () => Service.RegisterAsync(TenantId, Actor, Number, ApiUrl, null));

        await Assert.That(snapshots.Count).IsEqualTo(2);
        await Assert.That(snapshots[1][0].Action).IsEqualTo(RtSignalRegistrationActionEnum.RegisterFailed);
        await Assert.That(snapshots[1][0].Detail).Contains("Captcha required");
        await Assert.That(snapshots[1]
                .Count(e => e.Action == RtSignalRegistrationActionEnum.RegisterFailed))
            .IsEqualTo(1);
    }

    [Test]
    public async Task RetryWithCaptchaToken_ReachesTheBridge()
    {
        // The wizard's second attempt: same number, now with the token — the register flow allows
        // it because the failed first attempt left the state at Failed (re-register allowed).
        ArrangeChannel(RtSignalRegistrationStateEnum.Failed);

        await Service.RegisterAsync(TenantId, Actor, Number, null, "signalcaptcha://solved");

        await BridgeClient.Received(1).RegisterAsync(ApiUrl, Number, "signalcaptcha://solved");
    }
}
