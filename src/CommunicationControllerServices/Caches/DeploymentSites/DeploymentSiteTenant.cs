using System.Collections.Concurrent;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.DeploymentSites;

internal class DeploymentSiteTenant
{
    private readonly IDeploymentSiteCachePublish _poolCachePublish;
    public string TenantId { get; }

    private readonly ConcurrentDictionary<OctoObjectId, DeploymentSite> _poolsById;

    public IReadOnlyDictionary<OctoObjectId, DeploymentSite> DeploymentSitesById => _poolsById;

    public DeploymentSiteTenant(IDeploymentSiteCachePublish poolCachePublish, string tenantId)
    {
        _poolCachePublish = poolCachePublish;

        TenantId = tenantId;
        _poolsById = new ConcurrentDictionary<OctoObjectId, DeploymentSite>();
    }

    public DeploymentSite AddDeploymentSite(string deploymentSiteName, OctoObjectId poolRtId, string connectionId)
    {
        var deploymentSite = new DeploymentSite(_poolCachePublish, poolRtId, deploymentSiteName, connectionId);
        _poolsById.AddOrUpdate(poolRtId, _ => deploymentSite,
            (_, _) => deploymentSite);
        _poolCachePublish.PublishConfigurationAsync(TenantId);

        return deploymentSite;
    }

    public void RemoveDeploymentSite(OctoObjectId poolRtId)
    {
        if (_poolsById.TryRemove(poolRtId, out _))
        {
            _poolCachePublish.PublishConfigurationAsync(TenantId);
        }
    }

    public void Clear()
    {
        _poolsById.Clear();

        _poolCachePublish.PublishConfigurationAsync(TenantId);
    }
}
