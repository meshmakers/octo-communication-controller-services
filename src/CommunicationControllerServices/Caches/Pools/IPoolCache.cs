namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools;

internal interface IPoolCache
{
    Task InitializeAsync();
    PoolTenant AddOrUpdateTenant(string tenantId);
    void RemoveTenant(string tenantId);
    bool TryGetTenant(string tenantId, out PoolTenant? poolTenant);
    bool HasTenant(string tenantId);
    void PublishConfiguration();
}