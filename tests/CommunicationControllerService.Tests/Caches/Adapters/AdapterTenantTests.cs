using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Caches.Adapters;

// Pins the atomic, connection-conditional removes that fix the reconnect race (AB#4594):
// a stale unregister / disconnect from an OLD connection must never remove or null out an
// adapter that has already re-registered on a NEWER connection.
internal class AdapterTenantTests
{
    private const string TenantId = "tenantId";
    private const string OldConnectionId = "oldConnectionId";
    private const string NewConnectionId = "newConnectionId";

    private readonly IAdapterCachePublish _cachePublish = Substitute.For<IAdapterCachePublish>();

    private AdapterTenant CreateTenantWithAdapter(out RtEntityId adapterRtId, string connectionId)
    {
        var tenant = new AdapterTenant(_cachePublish, TenantId);
        adapterRtId = RtEntityCreator.CreateAdapter().ToRtEntityId();
        tenant.AddAdapter(adapterRtId, connectionId, new AdapterConfigurationDto(adapterRtId, null, []));
        return tenant;
    }

    [Test]
    public async Task RemoveAdapterIfConnection_MatchingConnection_RemovesAndReturnsTrue()
    {
        var tenant = CreateTenantWithAdapter(out var adapterRtId, OldConnectionId);

        var removed = tenant.RemoveAdapterIfConnection(adapterRtId, OldConnectionId);

        using var _ = Assert.Multiple();
        await Assert.That(removed).IsTrue();
        await Assert.That(tenant.AdapterById.ContainsKey(adapterRtId)).IsFalse();
    }

    [Test]
    public async Task RemoveAdapterIfConnection_NewerConnection_KeepsAdapterAndReturnsFalse()
    {
        // The adapter has already reconnected on a new connection; the stale remove must no-op.
        var tenant = CreateTenantWithAdapter(out var adapterRtId, OldConnectionId);
        tenant.UpdateConnectionId(adapterRtId, NewConnectionId);

        var removed = tenant.RemoveAdapterIfConnection(adapterRtId, OldConnectionId);

        using var _ = Assert.Multiple();
        await Assert.That(removed).IsFalse();
        await Assert.That(tenant.AdapterById.ContainsKey(adapterRtId)).IsTrue();
        await Assert.That(tenant.AdapterById[adapterRtId].ConnectionId).IsEqualTo(NewConnectionId);
    }

    [Test]
    public async Task RemoveConnectionIdIfConnection_MatchingConnection_ClearsAndReturnsTrue()
    {
        var tenant = CreateTenantWithAdapter(out var adapterRtId, OldConnectionId);

        var cleared = tenant.RemoveConnectionIdIfConnection(adapterRtId, OldConnectionId);

        using var _ = Assert.Multiple();
        await Assert.That(cleared).IsTrue();
        await Assert.That(tenant.AdapterById.ContainsKey(adapterRtId)).IsTrue();
        await Assert.That(tenant.AdapterById[adapterRtId].ConnectionId).IsNull();
    }

    [Test]
    public async Task RemoveConnectionIdIfConnection_NewerConnection_KeepsConnectionAndReturnsFalse()
    {
        var tenant = CreateTenantWithAdapter(out var adapterRtId, OldConnectionId);
        tenant.UpdateConnectionId(adapterRtId, NewConnectionId);

        var cleared = tenant.RemoveConnectionIdIfConnection(adapterRtId, OldConnectionId);

        using var _ = Assert.Multiple();
        await Assert.That(cleared).IsFalse();
        await Assert.That(tenant.AdapterById[adapterRtId].ConnectionId).IsEqualTo(NewConnectionId);
    }
}
