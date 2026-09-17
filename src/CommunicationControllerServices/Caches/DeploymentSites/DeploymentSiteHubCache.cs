using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.DeploymentSites;

internal class DeploymentSiteHubCache : IDeploymentSiteCachePublish, IDeploymentSiteCache
{
    private readonly ConcurrentDictionary<string, DeploymentSiteTenant> _tenantDescriptions = new();
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public DeploymentSiteTenant AddOrUpdateTenant(string tenantId)
    {
        if (!_tenantDescriptions.TryGetValue(tenantId, out var tenantDescription))
        {
            var adapterHubTenant = new DeploymentSiteTenant(this, tenantId);
            tenantDescription = _tenantDescriptions.AddOrUpdate(tenantId, _ => adapterHubTenant,
                (_, _) => adapterHubTenant);

            PublishConfigurationAsync(tenantId);
        }
        return tenantDescription;
    }

    public void RemoveTenant(string tenantId)
    {
        _tenantDescriptions.TryRemove(tenantId, out _);

        PublishConfigurationAsync(tenantId);
    }

    public bool TryGetTenant(string tenantId, [NotNullWhen(true)] out DeploymentSiteTenant? deploymentSiteTenant)
    {
        return _tenantDescriptions.TryGetValue(tenantId, out deploymentSiteTenant);
    }

    public bool HasTenant(string tenantId)
    {
        return _tenantDescriptions.ContainsKey(tenantId);
    }

    public Task PublishConfigurationAsync(string tenantId)
    {
        Logger.Info("Publishing DeploymentSiteHubCache configuration '{TenantId}'", tenantId);
        return Task.CompletedTask;
    }
}
