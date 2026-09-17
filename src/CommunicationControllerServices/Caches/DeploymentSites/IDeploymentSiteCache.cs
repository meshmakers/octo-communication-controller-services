using System.Diagnostics.CodeAnalysis;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.DeploymentSites;

internal interface IDeploymentSiteCache
{
    DeploymentSiteTenant AddOrUpdateTenant(string tenantId);
    void RemoveTenant(string tenantId);
    bool TryGetTenant(string tenantId, [NotNullWhen(true)] out DeploymentSiteTenant? deploymentSiteTenant);
    bool HasTenant(string tenantId);
}