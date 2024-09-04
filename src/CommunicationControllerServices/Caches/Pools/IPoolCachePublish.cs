using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools;

internal interface IPoolCachePublish
{
    Task PublishConfigurationAsync(string tenantId);
    
    void ReloadConfiguration(ComControllerPoolUpdate configuration);
}