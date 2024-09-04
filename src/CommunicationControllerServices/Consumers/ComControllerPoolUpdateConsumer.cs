using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Consumers;

internal class ComControllerPoolUpdateConsumer(IPoolCachePublish poolCachePublish)
    : IDistributedConsumer<ComControllerPoolUpdate>
{
    public Task ConsumeAsync(IDistributedContext<ComControllerPoolUpdate> context)
    {
        poolCachePublish.ReloadConfiguration(context.Message);

        return Task.CompletedTask;
    }
}