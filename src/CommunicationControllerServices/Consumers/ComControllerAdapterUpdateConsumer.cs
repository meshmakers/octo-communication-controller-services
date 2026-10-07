using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Consumers;

internal class ComControllerAdapterUpdateConsumer(ILogger<ComControllerAdapterUpdateConsumer> logger, IAdapterCachePublish adapterCachePublish,
    ControllerStartTime startTime)
    : IDistributedConsumer<ComControllerAdapterUpdate>
{
    public async Task ConsumeAsync(IDistributedContext<ComControllerAdapterUpdate> context)
    {
        logger.LogInformation("Com controller adapter update {TenantId}", context.Message.TenantId);
        try
        {
            if (startTime.IsBeforeStart(context.Message.Timestamp))
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