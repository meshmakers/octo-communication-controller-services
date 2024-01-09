using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools;

internal interface IPoolCachePublish
{
    public void PublishConfiguration(string tenantId);
    
    void ReloadConfiguration(ComControllerPoolUpdate configuration);
}