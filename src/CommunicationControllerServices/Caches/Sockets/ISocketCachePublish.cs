using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Sockets;

internal interface ISocketCachePublish
{
    void PublishConfiguration(string tenantId);

    void ReloadConfiguration(ComControllerSocketUpdate configuration);
}