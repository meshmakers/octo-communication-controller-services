using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.SignalChannelServiceTests;

/// <summary>
/// AB#5143 read flow: null without a definition (the controller turns that into 404), and the
/// bridge cross-check that degrades into bridgeRegistered = null + warning when the bridge is
/// unreachable.
/// </summary>
internal class GetChannelAsyncTests : SignalChannelServiceTestsBase
{
    [Test]
    public async Task NoChannel_ReturnsNull()
    {
        var dto = await Service.GetChannelAsync(TenantId);

        await Assert.That(dto).IsNull();
        await BridgeClient.DidNotReceiveWithAnyArgs().GetAccountsAsync(default!);
    }

    [Test]
    public async Task NumberOnBridge_BridgeRegisteredTrue()
    {
        var channel = ArrangeChannel(RtSignalRegistrationStateEnum.Registered);
        channel.RegisteredAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        BridgeClient.GetAccountsAsync(ApiUrl).Returns([OtherNumber, Number]);

        var dto = await Service.GetChannelAsync(TenantId);

        await Assert.That(dto).IsNotNull();
        await Assert.That(dto!.Number).IsEqualTo(Number);
        await Assert.That(dto.ApiUrl).IsEqualTo(ApiUrl);
        await Assert.That(dto.RegistrationState).IsEqualTo((int)RtSignalRegistrationStateEnum.Registered);
        await Assert.That(dto.RegisteredAt).IsEqualTo(channel.RegisteredAt);
        await Assert.That(dto.BridgeRegistered).IsTrue();
        await Assert.That(dto.Warning).IsNull();
    }

    [Test]
    public async Task NumberNotOnBridge_BridgeRegisteredFalse()
    {
        ArrangeChannel(RtSignalRegistrationStateEnum.CodePending);
        BridgeClient.GetAccountsAsync(ApiUrl).Returns([OtherNumber]);

        var dto = await Service.GetChannelAsync(TenantId);

        await Assert.That(dto!.BridgeRegistered).IsFalse();
        await Assert.That(dto.Warning).IsNull();
    }

    [Test]
    public async Task BridgeUnreachable_BridgeRegisteredNullWithWarning()
    {
        var channel = ArrangeChannel(RtSignalRegistrationStateEnum.Registered);
        channel.LastError = null;
        BridgeClient.GetAccountsAsync(ApiUrl).ThrowsAsync(BridgeUnreachable());

        var dto = await Service.GetChannelAsync(TenantId);

        // The persisted definition is still worth showing when the bridge is down.
        await Assert.That(dto).IsNotNull();
        await Assert.That(dto!.BridgeRegistered).IsNull();
        await Assert.That(dto.Warning).IsNotNull();
        await Assert.That(dto.RegistrationState).IsEqualTo((int)RtSignalRegistrationStateEnum.Registered);
    }
}
