using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Consumers;

internal class ComControllerAdapterUpdateConsumer(IAdapterCachePublish adapterCachePublish)
    : IDistributedConsumer<ComControllerAdapterUpdate>
{
    public Task ConsumeAsync(IDistributedContext<ComControllerAdapterUpdate> context)
    {
        adapterCachePublish.ReloadConfiguration(context.Message);

        return Task.CompletedTask;
    }
}