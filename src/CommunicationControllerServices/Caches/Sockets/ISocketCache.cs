namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Sockets;

internal interface ISocketCache
{
    Task InitializeAsync();
    SocketTenant AddOrUpdateTenant(string tenantId);
    void RemoveTenant(string tenantId);
    bool TryGetTenant(string tenantId, out SocketTenant? socketTenant);
    void PublishConfiguration();
}