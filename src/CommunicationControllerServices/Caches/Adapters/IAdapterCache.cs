using System.Diagnostics.CodeAnalysis;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;

internal interface IAdapterCache
{
    AdapterTenant AddOrUpdateTenant(string tenantId);
    void RemoveTenant(string tenantId);
    bool TryGetTenant(string tenantId, [NotNullWhen(true)] out AdapterTenant? adapterTenant);
}