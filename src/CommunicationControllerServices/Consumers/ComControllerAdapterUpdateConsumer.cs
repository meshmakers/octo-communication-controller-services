using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Consumers;

internal class ComControllerAdapterUpdateConsumer : IDistributedConsumer<ComControllerAdapterUpdate>
{
    private readonly IAdapterCachePublish _adapterCachePublish;

    public ComControllerAdapterUpdateConsumer(IAdapterCachePublish adapterCachePublish)
    {
        _adapterCachePublish = adapterCachePublish;
    }
    
    public Task ConsumeAsync(IDistributedContext<ComControllerAdapterUpdate> context)
    {
        _adapterCachePublish.ReloadConfiguration(context.Message);

        return Task.CompletedTask;
    }
}