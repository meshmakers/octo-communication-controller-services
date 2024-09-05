using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Consumers;

internal class ComControllerPoolUpdateConsumer(ILogger<ComControllerPoolUpdateConsumer> logger, IPoolCachePublish poolCachePublish)
    : IDistributedConsumer<ComControllerPoolUpdate>
{
    public async Task ConsumeAsync(IDistributedContext<ComControllerPoolUpdate> context)
    {
        logger.LogInformation("Com controller pool update {TenantId}", context.Message.TenantId);
        try
        {
            if (context.Message.Timestamp < Constants.StartTime)
            {
                logger.LogInformation("Ignoring old message");
                return;
            }
            await poolCachePublish.ReloadConfigurationAsync(context.Message);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Com controller pool update failed: {TenantId}", context.Message.TenantId);
        }
        finally
        {
            logger.LogInformation("Com controller pool update finished: {TenantId}", context.Message.TenantId);
        }
    }
}