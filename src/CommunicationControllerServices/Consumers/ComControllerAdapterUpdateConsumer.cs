using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Consumers;

internal class ComControllerAdapterUpdateConsumer(ILogger<ComControllerAdapterUpdateConsumer> logger, IAdapterCachePublish adapterCachePublish)
    : IDistributedConsumer<ComControllerAdapterUpdate>
{
    private static readonly DateTime StartDateTime = DateTime.Now;

    public async Task ConsumeAsync(IDistributedContext<ComControllerAdapterUpdate> context)
    {
        logger.LogInformation("Com controller adapter update {TenantId}", context.Message.TenantId);
        try
        {
            if (context.Message.Timestamp < StartDateTime)
            {
                logger.LogInformation("Ignoring old message");
                return;
            }
            await adapterCachePublish.ReloadConfigurationAsync(context.Message);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Com controller adapter update failed: {TenantId}", context.Message.TenantId);
        }
        finally
        {
            logger.LogInformation("Com controller adapter update finished: {TenantId}", context.Message.TenantId);
        }
    }
}