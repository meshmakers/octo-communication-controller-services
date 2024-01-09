using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Plugs;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Consumers;

internal class ComControllerPlugUpdateConsumer : IDistributedConsumer<ComControllerPlugUpdate>
{
    private readonly IPlugCachePublish _plugCachePublish;

    public ComControllerPlugUpdateConsumer(IPlugCachePublish plugCachePublish)
    {
        _plugCachePublish = plugCachePublish;
    }
    
    public Task ConsumeAsync(IDistributedContext<ComControllerPlugUpdate> context)
    {
        _plugCachePublish.ReloadConfiguration(context.Message);

        return Task.CompletedTask;
    }
}