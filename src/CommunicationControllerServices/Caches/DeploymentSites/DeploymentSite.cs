using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages.Payloads;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.DeploymentSites;

internal class DeploymentSite
{
    private readonly IDeploymentSiteCachePublish _poolCachePublish;

    public DeploymentSite(IDeploymentSiteCachePublish poolCachePublish, OctoObjectId deploymentSiteRtId, string deploymentSiteName, string connectionId)
    {
        _poolCachePublish = poolCachePublish;
        DeploymentSiteRtId = deploymentSiteRtId;
        DeploymentSiteName = deploymentSiteName;
        ConnectionId = connectionId;
    }

    public DeploymentSite(IDeploymentSiteCachePublish poolCachePublish, DeploymentSiteDescription deploymentSiteDescription)
    {
        _poolCachePublish = poolCachePublish;
        DeploymentSiteRtId = deploymentSiteDescription.DeploymentSiteRtId;
        DeploymentSiteName = deploymentSiteDescription.DeploymentSiteName;
        ConnectionId = deploymentSiteDescription.ConnectionId;
    }

    public string DeploymentSiteName { get; }
    public OctoObjectId DeploymentSiteRtId { get; }

    public string? ConnectionId { get; private set; }
    
    public void UpdateConnectionId(string tenantId, string connectionId)
    {
        ConnectionId = connectionId;
        _poolCachePublish.PublishConfigurationAsync(tenantId);
    }
    
    public void RemoveConnectionId(string tenantId)
    {
        ConnectionId = null;
        _poolCachePublish.PublishConfigurationAsync(tenantId);
    }

    public DeploymentSiteDescription GetDeploymentSiteDescription()
    {
        return new DeploymentSiteDescription
        {
            ConnectionId = ConnectionId,
            DeploymentSiteName = DeploymentSiteName,
            DeploymentSiteRtId = DeploymentSiteRtId
        };
    }
}