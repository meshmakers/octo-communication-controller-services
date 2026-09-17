namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.DeploymentSites;

internal interface IDeploymentSiteCachePublish
{
    Task PublishConfigurationAsync(string tenantId);
}
