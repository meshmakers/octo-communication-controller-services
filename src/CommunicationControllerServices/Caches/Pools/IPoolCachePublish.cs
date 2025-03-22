using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools;

internal interface IPoolCachePublish
{
    Task PublishConfigurationAsync(string tenantId);

    Task ReloadConfigurationAsync(ComControllerPoolUpdate configuration);
}
    
