namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Plugs;

internal interface IPlugCache
{
    PlugTenant AddOrUpdateTenant(string tenantId);
    void RemoveTenant(string tenantId);
    bool TryGetTenant(string tenantId, out PlugTenant? plugTenant);
}