using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Consumers;

internal class ComControllerPoolUpdateConsumer : IDistributedConsumer<ComControllerPoolUpdate>
{
    private readonly IPoolCachePublish _poolCachePublish;

    public ComControllerPoolUpdateConsumer(IPoolCachePublish poolCachePublish)
    {
        _poolCachePublish = poolCachePublish;
    }
    
    public Task ConsumeAsync(IDistributedContext<ComControllerPoolUpdate> context)
    {
        _poolCachePublish.ReloadConfiguration(context.Message);

        return Task.CompletedTask;
    }
}