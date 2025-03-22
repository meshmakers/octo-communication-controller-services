using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;

internal interface IAdapterCachePublish
{
    Task LoadConfigurationAsync(string tenantId);
    
    void PublishConfiguration(string tenantId);
    Task PublishConfigurationAsync(string tenantId);

    Task ReloadConfigurationAsync(ComControllerAdapterUpdate configuration);
}