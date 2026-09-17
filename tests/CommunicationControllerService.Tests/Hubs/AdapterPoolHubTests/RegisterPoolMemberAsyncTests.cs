using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using NSubstitute;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Microsoft.AspNetCore.SignalR;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Hubs.AdapterPoolHubTests;

/// <summary>
///     AB#4924 increment 6 — registration of a pool member on <c>/adapterPoolHub</c>.
/// </summary>
/// <remarks>
///     🔴 The check with no equivalent anywhere else is the tenant binding. The route carries no
///     tenant (that is what makes a pool member possible at all), so "which tenant owns the pool this
///     process belongs to" is answered by the connection's token and completed here, at the first
///     moment a declared pool exists to compare it against. A member registering into another
///     tenant's pool would receive leases carrying a third tenant's service-account secret.
/// </remarks>
internal class RegisterPoolMemberAsyncTests : AdapterPoolHubTestsBase
{
    private static PoolMemberRegistrationDto ARegistration(string adapterPoolTenantId = LenderTenantId,
        string poolRtId = AdapterPoolRtId, string memberId = MemberId) => new()
    {
        AdapterPoolTenantId = adapterPoolTenantId,
        AdapterPoolRtId = poolRtId,
        MemberId = memberId,
        NodeDescriptors =
        [
            new NodeDescriptorDto("SetJson", 1, "Transform", false, false, "{}"),
            new NodeDescriptorDto("ForEach", 1, "Control", false, true, "{}")
        ],
        PipelineSchemaJson = "{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\"}"
    };

    [Test]
    public async Task MemberOfItsOwnTenantsPool_ItsNodeDescriptorsReachTheController()
    {
        ArrangeConnectionTenant(LenderTenantId);

        await Hub.RegisterPoolMemberAsync(ARegistration());

        // 🔴 AB#4924: before this, PoolMemberRegistrationDto carried a NodeNames string list that no
        // member ever filled and no controller ever read, and AdapterPoolConnectionManager stored no
        // descriptors at all. A BORROWER's DeployPipeline therefore had nothing to resolve an
        // execution class or a pipeline schema against.
        var capabilities = ConnectionManager.TryGetAdapterPoolCapabilities(LenderTenantId, AdapterPoolRtId);

        using var _ = Assert.Multiple();
        await Assert.That(capabilities).IsNotNull();
        await Assert.That(capabilities!.NodeDescriptors.Select(d => d.NodeName)).Contains("SetJson");
        await Assert.That(capabilities.PipelineSchemaJson).IsNotNull();
    }

    [Test]
    public async Task MemberOfItsOwnTenantsPool_IsRegisteredAndBecomesLeasable()
    {
        ArrangeConnectionTenant(LenderTenantId);

        var result = await Hub.RegisterPoolMemberAsync(ARegistration());

        using var _ = Assert.Multiple();
        await Assert.That(result.Accepted).IsTrue();
        await Assert.That(result.MemberId).IsEqualTo(MemberId);
        await Assert.That(result.HeartbeatIntervalSeconds).IsGreaterThan(0);
        await Assert.That(ConnectionManager.GetMembers(LenderTenantId, AdapterPoolRtId)).Count().IsEqualTo(1);
    }

    /// <summary>
    ///     A member that proposed no id still has to be identifiable — every execution it serves
    ///     records the value as <c>LeasedOnMemberId</c>, and "" would make the per-member queue view
    ///     of concept §5 unreadable.
    /// </summary>
    [Test]
    public async Task BlankMemberId_FallsBackToTheConnectionId()
    {
        ArrangeConnectionTenant(LenderTenantId);

        var result = await Hub.RegisterPoolMemberAsync(ARegistration(memberId: "  "));

        await Assert.That(result.MemberId).IsEqualTo(ConnectionId);
    }

    /// <summary>
    ///     🔴 The binding, enforced. A credential of tenant X must not register members into tenant
    ///     Y's pool.
    /// </summary>
    [Test]
    public async Task MemberOfAnotherTenantsPool_IsRefused_WhenEnforcing()
    {
        AuthorizationOptions.Mode = AdapterPoolHubAuthorizationMode.Enforce;
        ArrangeConnectionTenant("someoneelse");

        await Assert.That(async () => await Hub.RegisterPoolMemberAsync(ARegistration()))
            .Throws<HubException>();
        await Assert.That(ConnectionManager.GetMembers(LenderTenantId, AdapterPoolRtId)).IsEmpty();
    }

    /// <summary>
    ///     Fail closed on an unattributable token: without a tenant the claim to belong to a pool
    ///     cannot be checked at all, and "cannot be checked" must not read as "accepted".
    /// </summary>
    [Test]
    public async Task ConnectionWithoutATokenTenant_IsRefused_WhenEnforcing()
    {
        AuthorizationOptions.Mode = AdapterPoolHubAuthorizationMode.Enforce;
        ArrangeConnectionTenant(null);

        await Assert.That(async () => await Hub.RegisterPoolMemberAsync(ARegistration()))
            .Throws<HubException>();
    }

    /// <summary>
    ///     The staging contract, identical to the other two gates: LogOnly changes no outcome. It is
    ///     what lets an environment be armed from configuration after its inventory has been read,
    ///     with no release.
    /// </summary>
    [Test]
    public async Task MemberOfAnotherTenantsPool_IsStillRegistered_InLogOnly()
    {
        AuthorizationOptions.Mode = AdapterPoolHubAuthorizationMode.LogOnly;
        ArrangeConnectionTenant("someoneelse");

        var result = await Hub.RegisterPoolMemberAsync(ARegistration());

        using var _ = Assert.Multiple();
        await Assert.That(result.Accepted).IsTrue();
        await Assert.That(ConnectionManager.GetMembers(LenderTenantId, AdapterPoolRtId)).Count().IsEqualTo(1);
    }

    [Test]
    public async Task TenantBinding_IsCaseInsensitive()
    {
        AuthorizationOptions.Mode = AdapterPoolHubAuthorizationMode.Enforce;
        ArrangeConnectionTenant("LENDER");

        var result = await Hub.RegisterPoolMemberAsync(ARegistration());

        await Assert.That(result.Accepted).IsTrue();
    }

    /// <summary>
    ///     A malformed pool reference is refused with a message naming the offending field, in every
    ///     mode — it is not an authorization question. Same rationale as
    ///     <c>OperatorHub.RegisterDeploymentSiteAsync</c>: a member cannot act on "'' is not a valid 24 digit
    ///     hex string" and would retry the same broken configuration forever.
    /// </summary>
    [Test]
    [Arguments("", AdapterPoolRtId)]
    [Arguments(LenderTenantId, "")]
    [Arguments(LenderTenantId, "not-a-hex-id")]
    public async Task MalformedPoolReference_IsRefused_InEveryMode(string adapterPoolTenantId, string poolRtId)
    {
        foreach (var mode in new[] { AdapterPoolHubAuthorizationMode.LogOnly, AdapterPoolHubAuthorizationMode.Enforce })
        {
            AuthorizationOptions.Mode = mode;
            ArrangeConnectionTenant(LenderTenantId);

            await Assert.That(async () =>
                    await Hub.RegisterPoolMemberAsync(ARegistration(adapterPoolTenantId, poolRtId)))
                .Throws<HubException>();
        }
    }

    /// <summary>
    ///     🔴 The <c>IShutdownState</c> guard. A member registered on a pod that is going away would
    ///     be handed a lease that dies with the pod, and the borrower's work would be interrupted for
    ///     no reason other than a rollout. Refusing sends the member round its reconnect loop to a
    ///     surviving pod.
    /// </summary>
    [Test]
    public async Task ShuttingDown_RefusesTheRegistrationWithoutThrowing()
    {
        ShutdownState.IsShuttingDown.Returns(true);
        ArrangeConnectionTenant(LenderTenantId);

        var result = await Hub.RegisterPoolMemberAsync(ARegistration());

        using var _ = Assert.Multiple();
        await Assert.That(result.Accepted).IsFalse();
        await Assert.That(result.StatusMessage).IsNotNull();
        // Not registered: a member this pod accepted would be offered leases it cannot serve.
        await Assert.That(ConnectionManager.GetMembers(LenderTenantId, AdapterPoolRtId)).IsEmpty();
    }
}
