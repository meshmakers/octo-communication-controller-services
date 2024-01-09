using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Sockets;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Consumers;

internal class ComControllerSocketUpdateConsumer : IDistributedConsumer<ComControllerSocketUpdate>
{
    private readonly ISocketCachePublish _socketCachePublish;

    public ComControllerSocketUpdateConsumer(ISocketCachePublish socketCachePublish)
    {
        _socketCachePublish = socketCachePublish;
    }
    
    public Task ConsumeAsync(IDistributedContext<ComControllerSocketUpdate> context)
    {
        _socketCachePublish.ReloadConfiguration(context.Message);

        return Task.CompletedTask;
    }
}