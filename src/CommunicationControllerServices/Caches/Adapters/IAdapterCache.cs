namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;

internal interface IAdapterCache
{
    AdapterTenant AddOrUpdateTenant(string tenantId);
    void RemoveTenant(string tenantId);
    bool TryGetTenant(string tenantId, out AdapterTenant? adapterTenant);
}