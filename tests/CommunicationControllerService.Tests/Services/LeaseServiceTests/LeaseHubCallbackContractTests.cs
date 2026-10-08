using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.LeaseServiceTests;

/// <summary>
///     N8 — what <c>LeaseService</c> pushes to a pool member must bind to the handler the member
///     registers from <see cref="IAdapterPoolHubCallbacks" />.
/// </summary>
/// <remarks>
///     🔴 <c>DrainMemberAsync</c> sent <c>DrainAsync</c> with no arguments while the member registers
///     <c>On&lt;string&gt;</c>. SignalR drops such an invocation on the client without an error on
///     either side: on test-2-dev (08.10.) an idle-shrink drain never reached the member, it stayed
///     Ready and never exited, the ReplicaSet removed the other pod on scale-down, and the pool sat
///     with zero usable members while the controller had logged "Told pool member … to drain". Every
///     controller drain (idle shrink, lease TTL) had been a no-op on the member since AB#4924.
/// </remarks>
internal class LeaseHubCallbackContractTests : LeaseServiceTestsBase
{
    [Test]
    public async Task DrainMember_SendsTheReason_TheMemberHandlerCanBind()
    {
        ArrangeGrantableLease();

        await LeaseService.DrainMemberAsync(ConnectionId, "idle for 30 minute(s); pool shrinks to 1");

        var (method, args) = SentToTheMember().Single();
        using var _ = Assert.Multiple();
        await Assert.That(method).IsEqualTo(nameof(IAdapterPoolHubCallbacks.DrainAsync));
        await Assert.That(HubCallbackContract.Violations(typeof(IAdapterPoolHubCallbacks), method, args)).IsEmpty();
        await Assert.That(args.Length).IsEqualTo(1);
        await Assert.That(args[0]).IsEqualTo("idle for 30 minute(s); pool shrinks to 1");
    }

    [Test]
    public async Task GrantLease_SendsALease_TheMemberHandlerCanBind()
    {
        ArrangeGrantableLease();

        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, ARequest("exec-1"));

        var (method, args) = SentToTheMember().Single();
        using var _ = Assert.Multiple();
        await Assert.That(result.Granted).IsTrue();
        await Assert.That(method).IsEqualTo(nameof(IAdapterPoolHubCallbacks.LeaseAsync));
        await Assert.That(HubCallbackContract.Violations(typeof(IAdapterPoolHubCallbacks), method, args)).IsEmpty();
    }

    private IReadOnlyList<(string Method, object?[] Args)> SentToTheMember() =>
        MemberProxy.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IClientProxy.SendCoreAsync))
            .Select(c => ((string)c.GetArguments()[0]!, (object?[])c.GetArguments()[1]!))
            .ToArray();
}
