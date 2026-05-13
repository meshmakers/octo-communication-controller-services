namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools;

internal interface IPoolCachePublish
{
    Task PublishConfigurationAsync(string tenantId);
}
