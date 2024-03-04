using System.Diagnostics.CodeAnalysis;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools;

internal interface IPoolCache
{
    PoolTenant AddOrUpdateTenant(string tenantId);
    void RemoveTenant(string tenantId);
    bool TryGetTenant(string tenantId, [NotNullWhen(true)] out PoolTenant? poolTenant);
    bool HasTenant(string tenantId);
}