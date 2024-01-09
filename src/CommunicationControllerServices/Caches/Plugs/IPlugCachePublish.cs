using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Plugs;

internal interface IPlugCachePublish
{
    void PublishConfiguration(string tenantId);

    void ReloadConfiguration(ComControllerPlugUpdate configuration);
}