using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;

internal interface IAdapterCachePublish
{
    void PublishConfiguration(string tenantId);

    void ReloadConfiguration(ComControllerAdapterUpdate configuration);
}